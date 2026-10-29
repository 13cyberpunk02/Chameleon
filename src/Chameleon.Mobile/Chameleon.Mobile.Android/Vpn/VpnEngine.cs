using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Chameleon.Core.Crypto;
using Chameleon.Core.Proxy;
using Chameleon.Core.Transport;
using Chameleon.Tls;

namespace Chameleon.Mobile.Android.Vpn;

/// <summary>
/// Поднимает ChameleonClient (локальный SOCKS5) из chameleon:// ссылки и держит его
/// живым: при обрыве сессии или смене сети (Wi-Fi ↔ LTE) переподключается сам.
/// tun2socks при этом не трогается - он смотрит на тот же SOCKS-порт, так что
/// короткий обрыв для приложений выглядит как несколько неудачных соединений.
/// </summary>
public sealed class VpnEngine : IAsyncDisposable
{
    public const int SocksPort = 1080;
    private const string Tag = "ChameleonVpn";

    private volatile ChameleonClient? _client;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ChameleonLink? _link;
    private KeyPair? _clientKey;
    private bool _nativeTls;
    private CancellationTokenSource? _cts;
    private Task? _watchdog;
    private TaskCompletionSource _kick = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int Port => SocksPort;
    public bool Running => _client is not null;

    // Статистика для UI (счётчики клиента; после переподключения начинаются заново).
    public long BytesUp => _client?.BytesSent ?? 0;
    public long BytesDown => _client?.BytesReceived ?? 0;
    public int RttMs => _client?.RttMs ?? 0;

    /// <summary>Разобрать ссылку и поднять клиента. Бросает, если первое подключение не удалось.</summary>
    public async Task StartAsync(string chameleonLink, bool nativeTls, CancellationToken ct)
    {
        if (!ChameleonLink.TryParse(chameleonLink, out var link, out string? err) || link is null)
            throw new InvalidOperationException("Неверная ссылка: " + err);

        _link = link;
        _nativeTls = nativeTls;
        _clientKey = !string.IsNullOrEmpty(link.ClientPrivateKeyHex)
            ? KeyFromPrivate(link.ClientPrivateKeyHex)
            : X25519.GenerateKeyPair();

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _client = await ConnectAsync(_cts.Token).ConfigureAwait(false);
        _watchdog = Task.Run(() => WatchdogAsync(_cts.Token));
    }

    /// <summary>
    /// Сменилась сеть. Старые TCP-несущие привязаны к прежнему интерфейсу и, как правило,
    /// не рвутся явно, а молча зависают - не ждём таймаутов, переподключаемся сразу.
    /// </summary>
    public void NetworkChanged()
    {
        global::Android.Util.Log.Info(Tag, "сеть сменилась - переподключаюсь");
        _kick.TrySetResult();
    }

    private async Task<ChameleonClient> ConnectAsync(CancellationToken ct)
    {
        var link = _link!;
        // DNS - на каждую попытку: в новой сети адрес сервера может резолвиться иначе.
        var addrs = await Dns.GetHostAddressesAsync(link.Host, ct).ConfigureAwait(false);
        var ip = Array.Find(addrs, a => a.AddressFamily == AddressFamily.InterNetwork) ?? addrs[0];
        var serverEp = new IPEndPoint(ip, link.Port);
        var socksEp = new IPEndPoint(IPAddress.Loopback, SocksPort);

        // ALPN в системном TLS Android есть только с Android 10 (API 29).
        bool native = _nativeTls && OperatingSystem.IsAndroidVersionAtLeast(29);
        CarrierWrapper carrier = native ? TlsCarrier.Client(link.Sni) : BcTlsCarrier.Client(link.Sni);

        var client = await ChameleonClient.StartAsync(
            serverEp, _clientKey!.Value, Convert.FromHexString(link.ServerPublicKeyHex), socksEp,
            carrier: carrier, cancellationToken: ct).ConfigureAwait(false);

        global::Android.Util.Log.Info(Tag,
            $"ChameleonClient поднят: SOCKS {socksEp}, сервер {serverEp}, sni {link.Sni}, " +
            $"tls={(native ? "system" : "bouncycastle")}, несущих {client.CarrierCount}");
        return client;
    }

    private async Task WatchdogAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            ChameleonClient? client = _client;
            if (client is null) return;

            try
            {
                await Task.WhenAny(client.Completion, _kick.Task).WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            _kick = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            global::Chameleon.Mobile.Vpn.Report(VpnStatus.Connecting);

            // Сеть на телефоне почти всегда возвращается (лифт, метро, смена вышки), поэтому
            // попытки не ограничены: пауза растёт 1 → 2 → 4 … до 15 с. Смена сети сбрасывает паузу.
            int delayMs = 1000;
            for (int attempt = 1; !ct.IsCancellationRequested; attempt++)
            {
                try
                {
                    await ReplaceClientAsync(ct).ConfigureAwait(false);
                    global::Android.Util.Log.Info(Tag, $"переподключено (попытка {attempt})");
                    global::Chameleon.Mobile.Vpn.Report(VpnStatus.Connected);
                    break;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception e)
                {
                    global::Android.Util.Log.Warn(Tag, $"попытка {attempt} не удалась: {e.Message}");
                }

                try
                {
                    var kick = _kick;
                    await Task.WhenAny(Task.Delay(delayMs, ct), kick.Task).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    if (kick.Task.IsCompleted)
                    {
                        _kick = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        delayMs = 1000;
                    }
                    else
                    {
                        delayMs = Math.Min(delayMs * 2, 15_000);
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    /// <summary>Закрыть текущего клиента (освобождает SOCKS-порт) и поднять нового.</summary>
    private async Task ReplaceClientAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_client is { } old)
            {
                _client = null;
                try
                {
                    await old.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    // ignored
                }
            }

            _client = await ConnectAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static KeyPair KeyFromPrivate(string privHex)
    {
        byte[] priv = Convert.FromHexString(privHex);
        return new KeyPair(priv, X25519.ScalarMultBase(priv));
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _cts?.Cancel();
        }
        catch
        {
            // ignored
        }

        if (_watchdog is not null)
        {
            try
            {
                await _watchdog.ConfigureAwait(false);
            }
            catch
            {
                // ignored
            }
        }

        if (_client is { } client)
        {
            _client = null;
            try
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // ignored
            }
        }

        _cts?.Dispose();
    }
}
