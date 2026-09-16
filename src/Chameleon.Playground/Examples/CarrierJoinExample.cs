using System.Net;
using Chameleon.Core.Crypto;
using Chameleon.Core.Proxy;

namespace Chameleon.Playground.Examples;

/// <summary>Присоединение несущих по сети: клиент поднимает 1 Noise + 2 join = 3 несущие в одной сессии.</summary>
public static class CarrierJoinExample
{
    public static async Task RunAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        int originPort = ExampleHelpers.StartHttpOrigin("Hello via multipath");
        KeyPair serverStatic = X25519.GenerateKeyPair();
        KeyPair clientStatic = X25519.GenerateKeyPair();

        await using var server = ChameleonServer.Start(new IPEndPoint(IPAddress.Loopback, 0), serverStatic);
        await using var client = await ChameleonClient.StartAsync(
            server.EndPoint, clientStatic, serverStatic.Public, new IPEndPoint(IPAddress.Loopback, 0),
            extraCarrierEndpoints: [server.EndPoint, server.EndPoint],
            cancellationToken: cts.Token);
        
        await Task.Delay(300);
        Console.WriteLine($"несущих в одной сессии: {client.CarrierCount} (1 Noise + 2 join)");
        Console.WriteLine("(в бою extraCarrierEndpoints - это другие IP/CDN/ретранслятор против whitelist)");

        using var http = new HttpClient(new SocketsHttpHandler
            { Proxy = new WebProxy($"socks5://127.0.0.1:{client.SocksEndPoint.Port}"), UseProxy = true });
        string body = "";
        for (int i = 0; i < 6; i++)
            body = await http.GetStringAsync($"http://127.0.0.1:{originPort}/req{i}", cts.Token);
        Console.WriteLine($"ответ: «{body}»");
        Console.WriteLine(client.CarrierCount == 3 && body.Contains("req5")
            ? "три несущие в одной сессии, прокси работает поверх мультипути ✓"
            : "что-то не так");
    }
}