// Клиент Chameleon (для Windows/Linux/mac). Поднимает локальный SOCKS5, который
// туннелирует через сервер по TLS с браузерным отпечатком (JA4 Chromium).
//
// Использование:
//   Chameleon.Client <server_host:port> <server_pubkey_hex> [socks=127.0.0.1:1080] [sni=www.example-cdn.com]
// Либо через переменные окружения:
//   CHAMELEON_SERVER, CHAMELEON_SERVER_KEY, CHAMELEON_SOCKS, CHAMELEON_SNI
//   CHAMELEON_EXTRA - доп. несущие через запятую (host:port,host:port) для мультипути

using System.Net;
using System.Net.Sockets;
using Chameleon.Core.Crypto;
using Chameleon.Core.Proxy;
using Chameleon.Tls;


string? server = Arg(0) ?? Environment.GetEnvironmentVariable("CHAMELEON_SERVER");
string? keyHex = Arg(1) ?? Environment.GetEnvironmentVariable("CHAMELEON_SERVER_KEY");
string socks = Arg(2) ?? Env("CHAMELEON_SOCKS", "127.0.0.1:1080");
string sni = Arg(3) ?? Env("CHAMELEON_SNI", "www.example-cdn.com");
string? extra = Environment.GetEnvironmentVariable("CHAMELEON_EXTRA");

if (server is null || keyHex is null)
{
    Console.WriteLine("Использование: Chameleon.Client <server_host:port> <server_pubkey_hex> [socks] [sni]");
    return 1;
}

byte[] serverPub = Convert.FromHexString(keyHex.Trim());
KeyPair clientStatic = X25519.GenerateKeyPair();

IPEndPoint serverEndpoint = await ResolveAsync(server);
IPEndPoint socksEndpoint = ParseLocal(socks);
IPEndPoint[]? extras = extra is { Length: > 0 }
    ? await Task.WhenAll(extra.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(ResolveAsync))
    : null;

Console.WriteLine("=== Chameleon client ===");
Console.WriteLine($"server : {serverEndpoint}  (sni {sni})");
if (extras is not null) Console.WriteLine($"extra  : {string.Join(", ", extras.AsEnumerable())}");

bool nativeTls = Environment.GetEnvironmentVariable("CHAMELEON_NATIVE_TLS") is "1" or "true";
await using var client = await ChameleonClient.StartAsync(
    serverEndpoint, clientStatic, serverPub, socksEndpoint,
    carrier: nativeTls ? Chameleon.Core.Transport.TlsCarrier.Client(sni) : BcTlsCarrier.Client(sni),
    extraCarrierEndpoints: extras);

Console.WriteLine($"SOCKS5-прокси слушает на {client.SocksEndPoint}");
Console.WriteLine($"несущих в сессии: {client.CarrierCount}");
Console.WriteLine("Настройте браузер/систему на этот SOCKS5. Ctrl+C для остановки.");

var stop = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.TrySetResult();
};
await stop.Task;
return 0;

static string? Arg(int i)
{
    var a = Environment.GetCommandLineArgs();
    int idx = i + 1;
    return idx < a.Length ? a[idx] : null;
}

static string Env(string n, string f) => Environment.GetEnvironmentVariable(n) is { Length: > 0 } v ? v : f;

static async Task<IPEndPoint> ResolveAsync(string hostPort)
{
    int i = hostPort.LastIndexOf(':');
    string host = hostPort[..i];
    int port = int.Parse(hostPort[(i + 1)..]);
    if (IPAddress.TryParse(host, out var ip)) return new IPEndPoint(ip, port);
    var addrs = await Dns.GetHostAddressesAsync(host);
    return new IPEndPoint(
        addrs.First(a =>
            a.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6), port);
}

static IPEndPoint ParseLocal(string s)
{
    int i = s.LastIndexOf(':');
    return new IPEndPoint(IPAddress.Parse(s[..i]), int.Parse(s[(i + 1)..]));
}