using System.Net;
using System.Net.Security;
using Chameleon.Core.Crypto;
using Chameleon.Core.Proxy;
using Chameleon.Core.Transport;
using Chameleon.Tls;

namespace Chameleon.Playground.Examples;

/// <summary>Прокси поверх настоящего TLS + декой-прокси: зонд без ключа видит реальный сайт.</summary>
public static class TlsProxyExample
{
    public static async Task RunAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        const string sni = "www.example-cdn.com";
        int originPort = ExampleHelpers.StartHttpOrigin("Hello from origin");
        int decoyPort = ExampleHelpers.StartHttpOrigin("Totally normal website");
        using var cert = ExampleHelpers.MakeSelfSignedCert(sni);
        KeyPair serverStatic = X25519.GenerateKeyPair();
        KeyPair clientStatic = X25519.GenerateKeyPair();

        await using var server = ChameleonServer.Start(
            new IPEndPoint(IPAddress.Loopback, 0), serverStatic,
            TlsCarrier.Server(cert), decoy: new DnsEndPoint("127.0.0.1", decoyPort));
        await using var client = await ChameleonClient.StartAsync(
            server.EndPoint, clientStatic, serverStatic.Public, new IPEndPoint(IPAddress.Loopback, 0),
            carrier: BcTlsCarrier.Client(sni), cancellationToken: cts.Token);

        using var http = new HttpClient(new SocketsHttpHandler
        { Proxy = new WebProxy($"socks5://127.0.0.1:{client.SocksEndPoint.Port}"), UseProxy = true });
        string tunneled = await http.GetStringAsync($"http://127.0.0.1:{originPort}/hello", cts.Token);
        Console.WriteLine($"клиент через туннель -> «{tunneled}»");

        using var probe = new HttpClient(new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions
            { TargetHost = sni, RemoteCertificateValidationCallback = (_, _, _, _) => true },
        });
        string probed = await probe.GetStringAsync($"https://127.0.0.1:{server.EndPoint.Port}/", cts.Token);
        Console.WriteLine($"зонд без ключа     -> «{probed}»");
        Console.WriteLine(tunneled.Contains("origin") && probed.Contains("normal website")
            ? "прикрытие работает: клиент идёт на цель, зонд видит декой" : "что-то не так");
    }
}