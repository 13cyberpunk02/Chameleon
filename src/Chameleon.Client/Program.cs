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

var server = Arg(0) ?? Environment.GetEnvironmentVariable("CHAMELEON_SERVER");
var keyHex = Arg(1) ?? Environment.GetEnvironmentVariable("CHAMELEON_SERVER_KEY");
var socks = Arg(2) ?? Env("CHAMELEON_SOCKS", "127.0.0.1:1080");
var sni = Arg(3) ?? Env("CHAMELEON_SNI", "www.example-cdn.com");
var extra = Environment.GetEnvironmentVariable("CHAMELEON_EXTRA");

if (server is null || keyHex is null)
{
    Console.WriteLine("Использование: Chameleon.Client <server_host:port> <server_pubkey_hex> [socks] [sni]");
    return 1;
}

var serverPub = Convert.FromHexString(keyHex.Trim());
var clientStatic = X25519.GenerateKeyPair();

var serverEndpoint = await ResolveAsync(server);
var socksEndpoint = ParseLocal(socks);
var extras = extra is { Length: > 0 }
    ? await Task.WhenAll(extra.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(ResolveAsync))
    : null;

Console.WriteLine("=== Chameleon client ===");
Console.WriteLine($"server : {serverEndpoint}  (sni {sni})");
if (extras is not null) Console.WriteLine($"extra  : {string.Join(", ", extras.AsEnumerable())}");

await using var client = await ChameleonClient.StartAsync(
    serverEndpoint, clientStatic, serverPub, socksEndpoint,
    carrier: BcTlsCarrier.Client(sni),
    extraCarrierEndpoints: extras);

Console.WriteLine($"SOCKS5-прокси слушает на {client.SocksEndPoint}");
Console.WriteLine($"несущих в сессии: {client.CarrierCount}");
Console.WriteLine("Настройте браузер/систему на этот SOCKS5. Ctrl+C для остановки.");

var stop = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.TrySetResult(); };
await stop.Task;
return 0;

static string? Arg(int i)
{
    var a = Environment.GetCommandLineArgs(); 
    var idx = i + 1; 
    return idx < a.Length ? a[idx] : null;
}
static string Env(string n, string f) => Environment.GetEnvironmentVariable(n) is { Length: > 0 } v ? v : f;

static async Task<IPEndPoint> ResolveAsync(string hostPort)
{
    var i = hostPort.LastIndexOf(':');
    var host = hostPort[..i]; 
    var port = int.Parse(hostPort[(i + 1)..]);
    if (IPAddress.TryParse(host, out var ip)) return new IPEndPoint(ip, port);
    var address = await Dns.GetHostAddressesAsync(host);
    return new IPEndPoint(address.First(a => a.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6), port);
}

static IPEndPoint ParseLocal(string s)
{
    var i = s.LastIndexOf(':');
    return new IPEndPoint(IPAddress.Parse(s[..i]), int.Parse(s[(i + 1)..]));
}
