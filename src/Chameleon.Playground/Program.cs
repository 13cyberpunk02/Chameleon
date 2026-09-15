using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using Chameleon.Core.Crypto;
using Chameleon.Core.Proxy;
using Chameleon.Core.Transport;

// Сквозная проверка ПОВЕРХ TLS: реальный HTTP-запрос через SOCKS5 → клиент → сервер → «сайт».
// Снаружи между клиентом и сервером - обычное TLS-соединение.
//
//   HttpClient ─socks5─▶ ChameleonClient ═══TLS(Noise+record)═══▶ ChameleonServer ─tcp─▶ origin

using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
const string sni = "www.example-cdn.com"; // домен прикрытия (SNI)

// 1. Локальный «сайт».
using var origin = new HttpListener();
int originPort = FreePort();
origin.Prefixes.Add($"http://127.0.0.1:{originPort}/");
origin.Start();
_ = Task.Run(async () =>
{
    while (origin.IsListening)
    {
        HttpListenerContext ctx;
        try
        {
            ctx = await origin.GetContextAsync();
        }
        catch
        {
            break;
        }

        byte[] body = Encoding.UTF8.GetBytes($"Hello from origin, path={ctx.Request.Url?.AbsolutePath}");
        ctx.Response.ContentType = "text/plain";
        ctx.Response.ContentLength64 = body.Length;
        await ctx.Response.OutputStream.WriteAsync(body);
        ctx.Response.Close();
    }
});
Console.WriteLine($"origin:  http://127.0.0.1:{originPort}/");

// 2. Ключи Noise + самоподписанный TLS-сертификат сервера.
KeyPair serverStatic = X25519.GenerateKeyPair();
KeyPair clientStatic = X25519.GenerateKeyPair();
using var certificate = TlsCarrier.CreateSelfSignedCertificate(sni);

// 3. Сервер с TLS-несущей.
await using var server = ChameleonServer.Start(
    new IPEndPoint(IPAddress.Loopback, 0), serverStatic, TlsCarrier.Server(certificate));
Console.WriteLine($"server:  {server.EndPoint} (TLS)");

// 4. Клиент с TLS-несущей; печатаем согласованные параметры TLS.
CarrierWrapper clientCarrier = TlsCarrier.Client(sni, onHandshake: tls =>
    Console.WriteLine(
        $"   TLS:  {tls.SslProtocol}, ALPN={tls.NegotiatedApplicationProtocol}, {tls.NegotiatedCipherSuite}"));

await using var client = await ChameleonClient.StartAsync(
    server.EndPoint, clientStatic, serverStatic.Public,
    new IPEndPoint(IPAddress.Loopback, 0), carrier: clientCarrier, cancellationToken: cts.Token);
Console.WriteLine($"socks5:  {client.SocksEndPoint}\n");

// 5. Реальные HTTP-запросы через SOCKS5 (две штуки - мультиплексирование).
var handler = new SocketsHttpHandler
{
    Proxy = new WebProxy($"socks5://127.0.0.1:{client.SocksEndPoint.Port}"),
    UseProxy = true,
};
using var http = new HttpClient(handler);

Console.WriteLine("→ GET /hello через TLS-туннель…");
string r1 = await http.GetStringAsync($"http://127.0.0.1:{originPort}/hello", cts.Token);
Console.WriteLine($"← «{r1}»");
string r2 = await http.GetStringAsync($"http://127.0.0.1:{originPort}/second", cts.Token);
Console.WriteLine($"← «{r2}»");

Console.WriteLine(r1.Contains("Hello from origin") && r2.Contains("second")
    ? "\nИТОГ: прокси работает поверх TLS, потоки мультиплексируются."
    : "\nИТОГ: что-то не так.");

// 6. Активный зонд: TLS есть, но ключа Noise нет - должен увидеть «просто сайт».
Console.WriteLine("\n--- активный зонд (TLS без ключа Noise) ---");
try
{
    using var probe = new HttpClient(new SocketsHttpHandler
    {
        SslOptions = new SslClientAuthenticationOptions
        {
            TargetHost = sni,
            RemoteCertificateValidationCallback = (_, _, _, _) => true,
        },
    });
    string coverPage = await probe.GetStringAsync($"https://127.0.0.1:{server.EndPoint.Port}/", cts.Token);
    Console.WriteLine(coverPage.Contains("It works")
        ? "зонд увидел обычную веб-страницу прикрытия (не распознал прокси)"
        : $"зонд получил: {coverPage[..Math.Min(60, coverPage.Length)]}");
}
catch (Exception e)
{
    Console.WriteLine($"зонд: {e.GetType().Name}");
}

origin.Stop();

static int FreePort()
{
    var l = new TcpListener(IPAddress.Loopback, 0);
    l.Start();
    int port = ((IPEndPoint)l.LocalEndpoint).Port;
    l.Stop();
    return port;
}