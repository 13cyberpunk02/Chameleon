using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using Chameleon.Core.Crypto;
using Chameleon.Core.Proxy;
using Chameleon.Core.Transport;

// Проверка TLS-туннеля + декой-прокси.
//   HttpClient ─socks5─▶ ChameleonClient ═══TLS═══▶ ChameleonServer ─tcp─▶ origin
//   Зонд ───────────────https без ключа──────────▶ ChameleonServer ─tcp─▶ ДЕКОЙ-сайт

using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
const string sni = "www.example-cdn.com";

int originPort = StartHttp("origin", "Hello from origin");
int decoyPort = StartHttp("decoy", "Totally normal website. Nothing to see here.");
Console.WriteLine($"origin:  http://127.0.0.1:{originPort}/  (цель прокси)");
Console.WriteLine($"decoy:   http://127.0.0.1:{decoyPort}/  (сайт-прикрытие)");

KeyPair serverStatic = X25519.GenerateKeyPair();
KeyPair clientStatic = X25519.GenerateKeyPair();
using var certificate = TlsCarrier.CreateSelfSignedCertificate(sni);

await using var server = ChameleonServer.Start(
    new IPEndPoint(IPAddress.Loopback, 0), serverStatic,
    TlsCarrier.Server(certificate),
    decoy: new DnsEndPoint("127.0.0.1", decoyPort));
Console.WriteLine($"server:  {server.EndPoint} (TLS, декой включён)");

CarrierWrapper clientCarrier = TlsCarrier.Client(sni, onHandshake: tls =>
    Console.WriteLine($"   TLS:  {tls.SslProtocol}, ALPN={tls.NegotiatedApplicationProtocol}"));

await using var client = await ChameleonClient.StartAsync(
    server.EndPoint, clientStatic, serverStatic.Public,
    new IPEndPoint(IPAddress.Loopback, 0), carrier: clientCarrier, cancellationToken: cts.Token);
Console.WriteLine($"socks5:  {client.SocksEndPoint}\n");

// 1) Легитимный клиент через туннель → должен попасть на origin.
var handler = new SocketsHttpHandler
{
    Proxy = new WebProxy($"socks5://127.0.0.1:{client.SocksEndPoint.Port}"),
    UseProxy = true,
};
using var http = new HttpClient(handler);
string tunneled = await http.GetStringAsync($"http://127.0.0.1:{originPort}/hello", cts.Token);
Console.WriteLine($"клиент через туннель → «{tunneled}»");

// 2) Активный зонд: TLS есть, ключа Noise нет → должен попасть на ДЕКОЙ.
string probed;
using (var probe = new HttpClient(new SocketsHttpHandler
       {
           SslOptions = new SslClientAuthenticationOptions
           {
               TargetHost = sni,
               RemoteCertificateValidationCallback = (_, _, _, _) => true,
           },
       }))
{
    probed = await probe.GetStringAsync($"https://127.0.0.1:{server.EndPoint.Port}/", cts.Token);
}

Console.WriteLine($"зонд без ключа     → «{probed}»");

bool ok = tunneled.Contains("Hello from origin")
          && probed.Contains("Totally normal website")
          && !probed.Contains("Hello from origin"); // зонд НЕ должен видеть настоящую цель
Console.WriteLine(ok
    ? "\nИТОГ: клиент идёт на origin, зонд видит декой-сайт. Прикрытие работает."
    : "\nИТОГ: что-то не так.");

static int StartHttp(string name, string message)
{
    int port = FreePort();
    var listener = new HttpListener();
    listener.Prefixes.Add($"http://127.0.0.1:{port}/");
    listener.Start();
    _ = Task.Run(async () =>
    {
        while (listener.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await listener.GetContextAsync();
            }
            catch
            {
                break;
            }

            byte[] body = Encoding.UTF8.GetBytes($"{message} (path={ctx.Request.Url?.AbsolutePath})");
            ctx.Response.ContentType = "text/plain";
            ctx.Response.ContentLength64 = body.Length;
            try
            {
                await ctx.Response.OutputStream.WriteAsync(body);
                ctx.Response.Close();
            }
            catch
            {
            }
        }
    });
    return port;
}

static int FreePort()
{
    var l = new TcpListener(IPAddress.Loopback, 0);
    l.Start();
    int port = ((IPEndPoint)l.LocalEndpoint).Port;
    l.Stop();
    return port;
}