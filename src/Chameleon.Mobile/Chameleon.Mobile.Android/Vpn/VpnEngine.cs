using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Chameleon.Core.Crypto;
using Chameleon.Core.Proxy;
using Chameleon.Tls;

namespace Chameleon.Mobile.Android.Vpn;

/// <summary>
/// Поднимает ChameleonClient (локальный SOCKS5) из chameleon:// ссылки.
/// Переиспычет Chameleon.Core как есть - чистый C#, работает на Android.
/// На этапе 3 к SocksPort будет подключён tun2socks (hev-socks5-tunnel.so).
/// </summary>
public sealed class VpnEngine : IAsyncDisposable
{
    public const int SocksPort = 1080;
    private ChameleonClient? _client;

    public int Port => SocksPort;
    public bool Running => _client is not null;

    // Статистика для UI (счётчики клиента).
    public long BytesUp => _client?.BytesSent ?? 0;
    public long BytesDown => _client?.BytesReceived ?? 0;
    public int RttMs => _client?.RttMs ?? 0;

    /// <summary>Разобрать ссылку и поднять клиента. Бросает при ошибке.</summary>
    public async Task StartAsync(string chameleonLink, CancellationToken ct)
    {
        if (!ChameleonLink.TryParse(chameleonLink, out var link, out string? err) || link is null)
            throw new InvalidOperationException("Неверная ссылка: " + err);
        
        var addrs = await Dns.GetHostAddressesAsync(link.Host, ct).ConfigureAwait(false);
        var ip = Array.Find(addrs, a => a.AddressFamily == AddressFamily.InterNetwork) ?? addrs[0];
        var serverEp = new IPEndPoint(ip, link.Port);
        
        KeyPair clientKey = !string.IsNullOrEmpty(link.ClientPrivateKeyHex)
            ? KeyFromPrivate(link.ClientPrivateKeyHex)
            : X25519.GenerateKeyPair();

        byte[] serverPub = Convert.FromHexString(link.ServerPublicKeyHex);
        var socksEp = new IPEndPoint(IPAddress.Loopback, SocksPort);

        _client = await ChameleonClient.StartAsync(
            serverEp, clientKey, serverPub, socksEp,
            carrier: BcTlsCarrier.Client(link.Sni),
            cancellationToken: ct).ConfigureAwait(false);

        global::Android.Util.Log.Info("ChameleonVpn",
            $"ChameleonClient поднят: SOCKS {socksEp}, сервер {serverEp}, sni {link.Sni}, несущих {_client.CarrierCount}");
    }

    private static KeyPair KeyFromPrivate(string privHex)
    {
        byte[] priv = Convert.FromHexString(privHex);
        return new KeyPair(priv, X25519.ScalarMultBase(priv));
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
        {
            try
            {
                await _client.DisposeAsync();
            }
            catch
            {
                // ignored
            }

            _client = null;
        }
    }
}