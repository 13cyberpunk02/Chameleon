using System.Net;
using System.Net.Sockets;
using Chameleon.Core.Crypto;
using Chameleon.Core.Session;
using Chameleon.Core.Transport;

namespace Chameleon.Core.Proxy;

/// <summary>
/// Клиент: держит одну сессию к серверу (возможно, по НЕСКОЛЬКИМ несущим) и
/// принимает локальные SOCKS5-подключения как потоки этой сессии.
/// </summary>
public sealed class ChameleonClient : IAsyncDisposable
{
    private readonly ChameleonSession _session;
    private readonly TcpListener _socksListener;
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptLoop;

    private ChameleonClient(ChameleonSession session, TcpListener socksListener)
    {
        _session = session;
        _socksListener = socksListener;
    }

    public IPEndPoint SocksEndPoint => (IPEndPoint)_socksListener.LocalEndpoint;
    public int CarrierCount => _session.CarrierCount;
    public long BytesSent => _session.BytesSent;
    public long BytesReceived => _session.BytesReceived;
    public int RttMs => _session.RttMs;

    /// <summary>Завершается, когда сессия оборвалась (все несущие мертвы).</summary>
    public Task Completion => _session.Completion;

    public static async Task<ChameleonClient> StartAsync(
        IPEndPoint serverEndPoint, KeyPair clientStatic, byte[] serverStaticPublic,
        IPEndPoint socksEndPoint, uint carrierId = 1, CarrierWrapper? carrier = null,
        TrafficShaper? shaper = null,
        IReadOnlyList<IPEndPoint>? extraCarrierEndpoints = null,
        CancellationToken cancellationToken = default)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(serverEndPoint, cancellationToken).ConfigureAwait(false);
        Stream stream = carrier is null
            ? new NetworkStream(socket, ownsSocket: true)
            : await carrier(socket, cancellationToken).ConfigureAwait(false);

        (ICarrierChannel channel, byte[] secret) = await ChameleonHandshake
            .ConnectAsync(stream, clientStatic, serverStaticPublic, carrierId, cancellationToken).ConfigureAwait(false);
        var session = ChameleonSession.Start(channel, isClient: true, shaper);

        if (extraCarrierEndpoints is not null)
        {
            uint nextCarrierId = carrierId + 1;
            foreach (var endpoint in extraCarrierEndpoints)
            {
                try
                {
                    await JoinCarrierAsync(session, endpoint, secret, nextCarrierId, carrier, cancellationToken)
                        .ConfigureAwait(false);
                    nextCarrierId++;
                }
                catch (Exception)
                {
                    /* дополнительная несущая необязательна */
                }
            }
        }

        var listener = new TcpListener(socksEndPoint);
        listener.Start();
        var client = new ChameleonClient(session, listener);
        client._acceptLoop = Task.Run(() => client.AcceptLoopAsync(client._cts.Token));
        return client;
    }

    private static async Task JoinCarrierAsync(
        ChameleonSession session, IPEndPoint endpoint, byte[] secret, uint carrierId,
        CarrierWrapper? carrier, CancellationToken cancellationToken)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
        Stream stream = carrier is null
            ? new NetworkStream(socket, ownsSocket: true)
            : await carrier(socket, cancellationToken).ConfigureAwait(false);

        byte[] join = CarrierJoin.BuildJoin(secret, carrierId);
        await Framing.WriteFrameAsync(stream, join, cancellationToken).ConfigureAwait(false);

        byte[] prefix = new byte[2];
        int len = await Framing.ReadPrefixAsync(stream, prefix, cancellationToken).ConfigureAwait(false);
        byte[] ack = await Framing.ReadExactCountAsync(stream, len, cancellationToken).ConfigureAwait(false);
        if (!CarrierJoin.VerifyAck(secret, GetNonce(join), ack))
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw new ChameleonProtocolException("Сервер не подтвердил присоединение несущей");
        }

        session.AddCarrier(new FrameChannel(stream));
    }

    private static byte[] GetNonce(byte[] join) => join.AsSpan(16 + 4, 16).ToArray();

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Socket socket;
            try
            {
                socket = await _socksListener.AcceptSocketAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            _ = HandleSocksAsync(socket, cancellationToken);
        }
    }

    private async Task HandleSocksAsync(Socket socket, CancellationToken cancellationToken)
    {
        try
        {
            var control = new NetworkStream(socket, ownsSocket: false);
            Socks5.Request req = await Socks5.ReadRequestAsync(control, cancellationToken).ConfigureAwait(false);

            if (req.Command == Socks5.Command.UdpAssociate)
            {
                await HandleUdpAssociateAsync(socket, control, cancellationToken).ConfigureAwait(false);
                return;
            }

            await Socks5.ReplyAsync(control, 0x00, new IPEndPoint(IPAddress.Any, 0), cancellationToken)
                .ConfigureAwait(false);
            ChameleonStream logical = await _session
                .OpenStreamAsync(req.Host, req.Port, cancellationToken: cancellationToken).ConfigureAwait(false);
            await Relay.RunAsync(socket, logical, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            try
            {
                socket.Dispose();
            }
            catch
            {
                // ignored
            }
        }
    }

    private async Task HandleUdpAssociateAsync(Socket control, NetworkStream controlStream,
        CancellationToken cancellationToken)
    {
        var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        udp.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var bound = (IPEndPoint)udp.LocalEndPoint!;

        await Socks5.ReplyAsync(controlStream, 0x00, bound, cancellationToken).ConfigureAwait(false);

        ChameleonStream logical = await _session
            .OpenStreamAsync("0.0.0.0", 0, Protocol.StreamKind.Udp, cancellationToken).ConfigureAwait(false);
        try
        {
            await UdpRelayClient.RunAsync(udp, controlStream, logical, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                udp.Dispose();
            }
            catch
            {
                // ignored
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _socksListener.Stop();
        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch
            {
                // ignored
            }
        }

        await _session.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
    }
}