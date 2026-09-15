using System.Net;
using System.Net.Sockets;
using System.Text;
using Chameleon.Core.Crypto;
using Chameleon.Core.Session;
using Chameleon.Core.Transport;

namespace Chameleon.Core.Proxy;

/// <summary>
/// Сервер: принимает несущие, проводит рукопожатие и для каждого потока клиента
/// открывает реальное TCP-соединение к запрошенному адресу.
/// Несущую задаёт <see cref="CarrierWrapper"/>: голый TCP или TLS.
/// </summary>
public sealed class ChameleonServer : IAsyncDisposable
{
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);

    private readonly TcpListener _listener;
    private readonly KeyPair _serverStatic;
    private readonly CarrierWrapper? _carrier;
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptLoop;

    private ChameleonServer(TcpListener listener, KeyPair serverStatic, CarrierWrapper? carrier)
    {
        _listener = listener;
        _serverStatic = serverStatic;
        _carrier = carrier;
    }

    public IPEndPoint EndPoint => (IPEndPoint)_listener.LocalEndpoint;

    public static ChameleonServer Start(IPEndPoint endPoint, KeyPair serverStatic, CarrierWrapper? carrier = null)
    {
        var listener = new TcpListener(endPoint);
        listener.Start();
        var server = new ChameleonServer(listener, serverStatic, carrier);
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
        Stream? carrierStream = null;
        ChameleonSession? session = null;
        try
        {
            carrierStream = _carrier is null
                ? new NetworkStream(socket, ownsSocket: true)
                : await _carrier(socket, cancellationToken).ConfigureAwait(false);

            RecordChannel channel;
            try
            {
                using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                handshakeCts.CancelAfter(HandshakeTimeout);
                (channel, _) = await ChameleonHandshake
                    .AcceptAsync(carrierStream, _serverStatic, carrierId: 1, handshakeCts.Token).ConfigureAwait(false);
            }
            catch (Exception) when (carrierStream is not null)
            {
                // Внутреннее рукопожатие не прошло: мусор или активный зонд.
                // Внутри TLS отвечаем как обычный веб-сервер, чтобы зонд увидел «просто сайт».
                await ServeCoverResponseAsync(carrierStream).ConfigureAwait(false);
                await carrierStream.DisposeAsync().ConfigureAwait(false);
                return;
            }

            session = ChameleonSession.Start(channel, isClient: false);
            session.StreamAccepted += stream => _ = DialAndRelayAsync(session, stream, cancellationToken);

            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // ignored
        }
        finally
        {
            if (session is not null) await session.DisposeAsync().ConfigureAwait(false);
            else if (carrierStream is not null) await carrierStream.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Заглушка прикрытия: минимальный HTTP-ответ. Полноценный вариант (обратный
    /// прокси на реальный сайт-декой, как в Reality) - следующий подэтап.
    /// </summary>
    private static async Task ServeCoverResponseAsync(Stream stream)
    {
        const string body =
            "<!doctype html><html><head><title>Welcome</title></head><body><h1>It works!</h1></body></html>";
        string response =
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: text/html; charset=utf-8\r\n" +
            $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n" +
            "Connection: close\r\n\r\n" + body;
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

        _cts.Dispose();
    }
}