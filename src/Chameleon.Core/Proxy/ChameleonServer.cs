using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Chameleon.Core.Crypto;
using Chameleon.Core.Session;
using Chameleon.Core.Transport;

namespace Chameleon.Core.Proxy;

/// <summary>
/// Сервер: принимает несущие. Входящее соединение - это либо НОВАЯ сессия (полное
/// рукопожатие Noise), либо ПРИСОЕДИНЕНИЕ несущей к существующей сессии (join),
/// либо чужак/зонд (декой-прокси). Сессии хранятся в реестре по session_id.
/// </summary>
public sealed class ChameleonServer : IAsyncDisposable
{
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);

    private sealed record Registered(ChameleonSession Session, byte[] Secret);

    private readonly TcpListener _listener;
    private readonly KeyPair _serverStatic;
    private readonly CarrierWrapper? _carrier;
    private readonly DnsEndPoint? _decoy;
    private readonly TrafficShaper? _shaper;
    private readonly ServerEventLog? _events;
    private readonly ConcurrentDictionary<string, Registered> _sessions = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptLoop;

    private ChameleonServer(TcpListener listener, KeyPair serverStatic, CarrierWrapper? carrier, DnsEndPoint? decoy,
        TrafficShaper? shaper, ServerEventLog? events)
    {
        _listener = listener;
        _serverStatic = serverStatic;
        _carrier = carrier;
        _decoy = decoy;
        _shaper = shaper;
        _events = events;
    }

    public IPEndPoint EndPoint => (IPEndPoint)_listener.LocalEndpoint;

    public static ChameleonServer Start(
        IPEndPoint endPoint, KeyPair serverStatic, CarrierWrapper? carrier = null, DnsEndPoint? decoy = null,
        TrafficShaper? shaper = null, ServerEventLog? events = null)
    {
        var listener = new TcpListener(endPoint);
        listener.Start();
        var server = new ChameleonServer(listener, serverStatic, carrier, decoy, shaper, events);
        server._acceptLoop = Task.Run(() => server.AcceptLoopAsync(server._cts.Token));
        return server;
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Socket socket;
            try
            {
                socket = await _listener.AcceptSocketAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            _ = HandleCarrierAsync(socket, cancellationToken);
        }
    }

    private async Task HandleCarrierAsync(Socket socket, CancellationToken cancellationToken)
    {
        string remoteIp = (socket.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "?";
        Stream? carrier = null;
        try
        {
            carrier = _carrier is null
                ? new NetworkStream(socket, ownsSocket: true)
                : await _carrier(socket, cancellationToken).ConfigureAwait(false);

            using var hs = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            hs.CancelAfter(HandshakeTimeout);

            var buffered = new List<byte>(2);
            byte[] prefix = new byte[2];
            if (!await ReadRecordingAsync(carrier, prefix, buffered, hs.Token).ConfigureAwait(false))
            {
                LogProbe(remoteIp, "нет данных");
                await Cover(carrier, buffered.ToArray(), cancellationToken).ConfigureAwait(false);
                return;
            }

            int len = BinaryPrimitives.ReadUInt16BigEndian(prefix);

            if (len == CarrierJoin.MessageLength)
            {
                await HandleJoinAsync(carrier, remoteIp, hs.Token).ConfigureAwait(false);
                return;
            }

            if (len == NoiseIkHandshake.Message1Length)
            {
                await HandleNewSessionAsync(carrier, prefix, len, remoteIp, cancellationToken).ConfigureAwait(false);
                return;
            }

            LogProbe(remoteIp, "не Noise/не join");
            await Cover(carrier, [.. buffered], cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            if (carrier is not null)
            {
                try
                {
                    await carrier.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    // ignored
                }
            }
        }
    }

    private async Task HandleNewSessionAsync(Stream carrier, byte[] prefix, int len, string remoteIp,
        CancellationToken cancellationToken)
    {
        byte[] message1 = await Framing.ReadExactCountAsync(carrier, len, cancellationToken).ConfigureAwait(false);
        var handshake = NoiseIkHandshake.CreateResponder(_serverStatic);
        try
        {
            handshake.ReadMessage1(message1);
        }
        catch (ChameleonProtocolException)
        {
            LogProbe(remoteIp, "Noise не сошёлся");
            await Cover(carrier, [.. prefix, .. message1], cancellationToken).ConfigureAwait(false);
            return;
        }

        var result = handshake.WriteMessage2(out byte[] message2);
        await Framing.WriteFrameAsync(carrier, message2, cancellationToken).ConfigureAwait(false);

        var channel = new FrameChannel(carrier);
        var session = ChameleonSession.Start(channel, isClient: false, _shaper);
        session.StreamAccepted += stream => _ = DialAndRelayAsync(session, stream, _cts.Token);

        string sessionId = Convert.ToHexString(CarrierJoin.SessionId(result.SessionSecret));
        _sessions[sessionId] = new Registered(session, result.SessionSecret);
        string clientKey = Convert.ToHexString(result.RemoteStaticPublic)[..16].ToLowerInvariant();
        _events?.Add("connect", remoteIp, $"новая сессия, client={clientKey}…, активных={_sessions.Count}");
        var startedAt = DateTime.UtcNow;

        try
        {
            await session.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _sessions.TryRemove(sessionId, out _);
            var dur = DateTime.UtcNow - startedAt;
            _events?.Add("disconnect", remoteIp,
                $"сессия закрыта, {FormatDuration(dur)}, ↑{Human(session.BytesReceived)} ↓{Human(session.BytesSent)}, активных={_sessions.Count}");
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task HandleJoinAsync(Stream carrier, string remoteIp, CancellationToken cancellationToken)
    {
        byte[] body = await Framing.ReadExactCountAsync(carrier, CarrierJoin.MessageLength, cancellationToken)
            .ConfigureAwait(false);
        var request = CarrierJoin.Parse(body);
        string sessionId = Convert.ToHexString(request.SessionId);

        if (!_sessions.TryGetValue(sessionId, out var reg) || !CarrierJoin.Verify(reg.Secret, request))
        {
            await carrier.DisposeAsync().ConfigureAwait(false);
            return;
        }

        byte[] ack = CarrierJoin.BuildAck(reg.Secret, request.Nonce);
        await Framing.WriteFrameAsync(carrier, ack, cancellationToken).ConfigureAwait(false);

        reg.Session.AddCarrier(new FrameChannel(carrier));
        _events?.Add("join", remoteIp, $"присоединена несущая (carrier {request.CarrierId})");
    }

    private async Task Cover(Stream carrier, byte[] buffered, CancellationToken cancellationToken)
    {
        if (_decoy is null)
        {
            await ServeStaticPageAsync(carrier).ConfigureAwait(false);
            await carrier.DisposeAsync().ConfigureAwait(false);
            return;
        }

        try
        {
            using var decoy = new TcpClient();
            await decoy.ConnectAsync(_decoy.Host, _decoy.Port, cancellationToken).ConfigureAwait(false);
            Stream d = decoy.GetStream();
            if (buffered.Length > 0) await d.WriteAsync(buffered, cancellationToken).ConfigureAwait(false);
            await Task.WhenAny(carrier.CopyToAsync(d, cancellationToken), d.CopyToAsync(carrier, cancellationToken))
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            await ServeStaticPageAsync(carrier).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await carrier.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // ignored
            }
        }
    }

    private static async Task ServeStaticPageAsync(Stream stream)
    {
        const string body =
            "<!doctype html><html><head><title>Welcome</title></head><body><h1>It works!</h1></body></html>";
        string response = "HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\n" +
                          $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n" + body;
        try
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes(response)).ConfigureAwait(false);
        }
        catch
        {
            // ignored
        }
    }

    private static async Task DialAndRelayAsync(ChameleonSession session, ChameleonStream stream,
        CancellationToken cancellationToken)
    {
        if (stream.Kind == Protocol.StreamKind.Udp)
        {
            try
            {
                await UdpRelayServer.RunAsync(stream, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                try
                {
                    await session.ResetStreamAsync(stream.Id, cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // ignored
                }
            }

            return;
        }

        var target = new TcpClient();
        try
        {
            await target.ConnectAsync(stream.TargetHost!, stream.TargetPort, cancellationToken).ConfigureAwait(false);
            await Relay.RunAsync(target.Client, stream, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            try
            {
                await session.ResetStreamAsync(stream.Id, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // ignored
            }
        }
        finally
        {
            try
            {
                target.Dispose();
            }
            catch
            {
                // ignored
            }
        }
    }

    private void LogProbe(string remoteIp, string why)
        => _events?.Add("probe", remoteIp, $"чужак/зонд -> декой ({why})");

    private static string Human(long bytes)
    {
        string[] u = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes;
        int k = 0;
        while (v >= 1024 && k < u.Length - 1)
        {
            v /= 1024;
            k++;
        }

        return $"{v:0.#}{u[k]}";
    }

    private static string FormatDuration(TimeSpan t)
        => t.TotalHours >= 1 ? $"{(int)t.TotalHours}h{t.Minutes:00}m" :
            t.TotalMinutes >= 1 ? $"{t.Minutes}m{t.Seconds:00}s" : $"{t.Seconds}s";

    private static async Task<bool> ReadRecordingAsync(Stream stream, byte[] buffer, List<byte> recorder,
        CancellationToken cancellationToken)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken).ConfigureAwait(false);
            if (n == 0) return false;
            read += n;
        }

        recorder.AddRange(buffer);
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
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

        foreach (var reg in _sessions.Values) await reg.Session.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
    }
}