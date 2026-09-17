using System.Net;
using Chameleon.Core.Crypto;
using Chameleon.Core.Proxy;

namespace Chameleon.Playground.Examples;

/// <summary>Сквозной SOCKS5-прокси поверх голого TCP: HttpClient -> клиент -> сервер -> сайт.</summary>
public static class Socks5ProxyExample
{
    public static async Task RunAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        int originPort = ExampleHelpers.StartHttpOrigin("Hello from origin");
        KeyPair serverStatic = X25519.GenerateKeyPair();
        KeyPair clientStatic = X25519.GenerateKeyPair();

        await using var server = ChameleonServer.Start(new IPEndPoint(IPAddress.Loopback, 0), serverStatic);
        await using var client = await ChameleonClient.StartAsync(
            server.EndPoint, clientStatic, serverStatic.Public, new IPEndPoint(IPAddress.Loopback, 0),
            cancellationToken: cts.Token);
        Console.WriteLine($"SOCKS5 слушает на {client.SocksEndPoint}");

        using var http = new HttpClient(new SocketsHttpHandler
        {
            Proxy = new WebProxy($"socks5://127.0.0.1:{client.SocksEndPoint.Port}"),
            UseProxy = true,
        });
        string r1 = await http.GetStringAsync($"http://127.0.0.1:{originPort}/hello", cts.Token);
        string r2 = await http.GetStringAsync($"http://127.0.0.1:{originPort}/second", cts.Token);
        Console.WriteLine($"ответ 1: «{r1}»");
        Console.WriteLine($"ответ 2: «{r2}» (та же сессия - потоки мультиплексируются)");
    }
}