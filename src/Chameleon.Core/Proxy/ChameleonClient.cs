using System.Net;
using System.Net.Sockets;
using Chameleon.Core.Crypto;
using Chameleon.Core.Session;
using Chameleon.Core.Transport;

namespace Chameleon.Core.Proxy;

/// <summary>
/// Клиент: держит одну сессию к серверу и принимает локальные SOCKS5-подключения,
/// каждое из которых становится отдельным потоком внутри этой сессии.
/// Несущую задаёт <see cref="CarrierWrapper"/>: голый TCP или TLS.
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

    public static async Task<ChameleonClient> StartAsync(
        IPEndPoint serverEndPoint, KeyPair clientStatic, byte[] serverStaticPublic,
        IPEndPoint socksEndPoint, uint carrierId = 1, CarrierWrapper? carrier = null,
        TrafficShaper? shaper = null, CancellationToken cancellationToken = default)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(serverEndPoint, cancellationToken).ConfigureAwait(false);

        Stream stream = carrier is null
            ? new NetworkStream(socket, ownsSocket: true)
            : await carrier(socket, cancellationToken).ConfigureAwait(false);

        RecordChannel channel = await ChameleonHandshake.ConnectAsync(
            stream, clientStatic, serverStaticPublic, carrierId, cancellationToken).ConfigureAwait(false);

        var session = ChameleonSession.Start(channel, isClient: true, shaper);

        var listener = new TcpListener(socksEndPoint);
        listener.Start();

        var client = new ChameleonClient(session, listener);
        client._acceptLoop = Task.Run(() => client.AcceptLoopAsync(client._cts.Token));
        return client;
    }

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
            var stream = new NetworkStream(socket, ownsSocket: false);
            Socks5.Target target = await Socks5.HandshakeAsync(stream, cancellationToken).ConfigureAwait(false);

            ChameleonStream logical = await _session
                .OpenStreamAsync(target.Host, target.Port, cancellationToken: cancellationToken).ConfigureAwait(false);

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