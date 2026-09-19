using System.Security.Cryptography;
using Chameleon.Core.Crypto;
using Chameleon.Core.Session;
using Chameleon.Core.Transport;

namespace Chameleon.Playground.Examples;

/// <summary>Мультипуть-движок: две несущие, одну убиваем посреди передачи - данные доходят целыми.</summary>
public static class MultipathExample
{
    public static async Task RunAsync()
    {
        var (cA, sA) = DuplexStream.CreatePair();
        var (cB, sB) = DuplexStream.CreatePair();

        static FrameChannel Ch(Stream s) => new(s);

        await using var server = ChameleonSession.Start(Ch(sA), isClient: false);
        server.AddCarrier(Ch(sB));

        var received = new MemoryStream();
        var done = new TaskCompletionSource();
        server.StreamAccepted += st => _ = Task.Run(async () =>
        {
            while (true)
            {
                var r = await st.Input.ReadAsync();
                foreach (var seg in r.Buffer) received.Write(seg.Span);
                st.Input.AdvanceTo(r.Buffer.End);
                if (r.IsCompleted) break;
            }

            done.TrySetResult();
        });

        await using var client = ChameleonSession.Start(Ch(cA), isClient: true);
        client.AddCarrier(Ch(cB));
        Console.WriteLine($"несущих: клиент={client.CarrierCount}, сервер={server.CarrierCount}");

        byte[] payload = RandomNumberGenerator.GetBytes(400_000);
        string expected = Convert.ToHexString(SHA256.HashData(payload));
        var stream = await client.OpenStreamAsync("example.com", 443);
        Console.WriteLine("шлём 400 КБ, посреди передачи убиваем несущую A…");

        int off = 0;
        while (off < payload.Length)
        {
            int n = Math.Min(8000, payload.Length - off);
            await stream.WriteAsync(payload.AsMemory(off, n));
            off += n;
            if (off >= payload.Length / 3 && !cA.Killed)
            {
                cA.Kill();
                sA.Kill();
                Console.WriteLine($"  несущая A убита на {off} байт");
            }
        }

        await stream.CompleteOutputAsync();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(15));

        string actual = Convert.ToHexString(SHA256.HashData(received.ToArray()));
        Console.WriteLine($"принято {received.Length}/{payload.Length} байт, живых несущих={client.CarrierCount}");
        Console.WriteLine(received.Length == payload.Length && actual == expected
            ? "все данные дошли целыми несмотря на смерть несущей и переупорядочивание"
            : "данные повреждены");
    }
}