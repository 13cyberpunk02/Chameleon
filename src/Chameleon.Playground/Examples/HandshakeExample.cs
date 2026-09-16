using System.Net;
using System.Net.Sockets;
using System.Text;
using Chameleon.Core;
using Chameleon.Core.Crypto;
using Chameleon.Core.Protocol;
using Chameleon.Core.Transport;

namespace Chameleon.Playground.Examples;

/// <summary>Рукопожатие Noise IK, обмен пакетом поверх record-слоя, отпор активному зонду.</summary>
public static class HandshakeExample
{
    public static async Task RunAsync()
    {
        KeyPair serverStatic = X25519.GenerateKeyPair();
        KeyPair clientStatic = X25519.GenerateKeyPair();

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var server = Task.Run(async () =>
        {
            using var tcp = await listener.AcceptTcpClientAsync();
            try
            {
                var outcome = await ChameleonHandshake.TryAcceptAsync(tcp.GetStream(), serverStatic, carrierId: 1);
                if (!outcome.Succeeded) { Console.WriteLine("[server] зонд отвергнут"); return; }
                Console.WriteLine($"[server] клиент аутентифицирован: {Convert.ToHexString(outcome.ClientStaticPublic!)[..16]}…");
                await using var channel = outcome.Channel!;
                byte[] buf = new byte[RecordFormat.MaxPlaintext];
                int n = await channel.ReadRecordAsync(buf);
                var frames = new List<Frame>();
                PacketReader.Parse(buf.AsMemory(0, n), frames);
                foreach (var f in frames)
                    Console.WriteLine(f switch
                    {
                        StreamOpenFrame o => $"[server]   STREAM_OPEN -> {o.Host}:{o.Port}",
                        StreamFrame s => $"[server]   STREAM «{Encoding.UTF8.GetString(s.Data.Span)}»",
                        _ => $"[server]   {f}",
                    });
            }
            catch (ChameleonProtocolException e) { Console.WriteLine($"[server] отказ: {e.Message}"); }
        });

        using (var tcp = new TcpClient())
        {
            await tcp.ConnectAsync(IPAddress.Loopback, port);
            var (channel, _) = await ChameleonHandshake.ConnectAsync(tcp.GetStream(), clientStatic, serverStatic.Public, 1);
            await using (channel)
            {
                Console.WriteLine("[client] рукопожатие Noise IK завершено");
                byte[] packet = new byte[256];
                int len = BuildRequest(packet);
                await channel.WriteRecordAsync(packet.AsMemory(0, len));
                Console.WriteLine("[client] отправлен зашифрованный пакет");
                await Task.Delay(200);
            }
        }
        await server;

        // Зонд без ключа сервера.
        Console.WriteLine("\n--- активный зонд (случайный мусор) ---");
        var l2 = new TcpListener(IPAddress.Loopback, 0); l2.Start();
        int p2 = ((IPEndPoint)l2.LocalEndpoint).Port;
        var probeServer = Task.Run(async () =>
        {
            using var tcp = await l2.AcceptTcpClientAsync();
            var outcome = await ChameleonHandshake.TryAcceptAsync(tcp.GetStream(), serverStatic, 1);
            Console.WriteLine(outcome.Succeeded ? "[server] ОШИБКА: зонд прошёл" : "[server] зонд не распознан как клиент (пошёл бы в декой)");
        });
        using (var probe = new TcpClient())
        {
            await probe.ConnectAsync(IPAddress.Loopback, p2);
            byte[] junk = new byte[98];
            Random.Shared.NextBytes(junk);
            junk[0] = 0; junk[1] = 96;
            await probe.GetStream().WriteAsync(junk);
            await probe.GetStream().FlushAsync();
            await Task.Delay(200);
        }
        await probeServer;
        listener.Stop(); l2.Stop();

        static int BuildRequest(Span<byte> b)
        {
            var w = new PacketWriter(b, 0);
            w.WriteStreamOpen(1, StreamKind.Tcp, "example.com", 443);
            w.WriteStream(1, 0, "GET / HTTP/1.1"u8);
            return w.Length;
        }
    }
}
