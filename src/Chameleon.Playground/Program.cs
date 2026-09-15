using System.Net;
using System.Net.Sockets;
using System.Text;
using Chameleon.Core;
using Chameleon.Core.Crypto;
using Chameleon.Core.Protocol;
using Chameleon.Core.Transport;

// Пара ключей сервера. Публичный ключ клиент знает заранее (как в VLESS/Reality),
// приватный есть только у сервера. carrier_id = 1 (позже несущих будет несколько).
KeyPair serverStatic = X25519.GenerateKeyPair();
KeyPair clientStatic = X25519.GenerateKeyPair();
uint carrierId = 1;

Console.WriteLine($"server pub: {Convert.ToHexString(serverStatic.Public)[..16]}…");
Console.WriteLine($"client pub: {Convert.ToHexString(clientStatic.Public)[..16]}…\n");

var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start();
int port = ((IPEndPoint)listener.LocalEndpoint).Port;

Task server = RunServerAsync(listener, serverStatic, carrierId);
await RunClientAsync(port, clientStatic, serverStatic.Public, carrierId);
await server;

await ProbeTestAsync(serverStatic, carrierId);

listener.Stop();

async Task RunClientAsync(int p, KeyPair clientKeys, byte[] serverPub, uint carrier)
{
    using var tcp = new TcpClient();
    await tcp.ConnectAsync(IPAddress.Loopback, p);
    await using var channel = await ChameleonHandshake.ConnectAsync(tcp.GetStream(), clientKeys, serverPub, carrier);
    Console.WriteLine("[client] рукопожатие Noise IK завершено, сессионные ключи получены");

    await channel.WriteRecordAsync(BuildRequestPacket());
    Console.WriteLine("[client] отправлен зашифрованный пакет через согласованные ключи");

    byte[] buffer = new byte[RecordFormat.MaxPlaintext];
    int n = await channel.ReadRecordAsync(buffer);
    var frames = new List<Frame>();
    PacketReader.Parse(buffer.AsMemory(0, n), frames);
    Console.WriteLine($"[client] получен ответ, фреймов: {frames.Count} ({frames[0]})");
}

async Task RunServerAsync(TcpListener l, KeyPair serverKeys, uint carrier)
{
    using var tcp = await l.AcceptTcpClientAsync();
    RecordChannel channel;
    byte[] clientIdentity;
    try
    {
        (channel, clientIdentity) = await ChameleonHandshake.AcceptAsync(tcp.GetStream(), serverKeys, carrier);
    }
    catch (ChameleonProtocolException e)
    {
        Console.WriteLine($"[server] рукопожатие отвергнуто: {e.Message}");
        return;
    }

    await using (channel)
    {
        Console.WriteLine($"[server] клиент аутентифицирован: {Convert.ToHexString(clientIdentity)[..16]}…");

        byte[] buffer = new byte[RecordFormat.MaxPlaintext];
        int n = await channel.ReadRecordAsync(buffer);
        var frames = new List<Frame>();
        PacketReader.Parse(buffer.AsMemory(0, n), frames);

        foreach (var frame in frames)
            Console.WriteLine(frame switch
            {
                StreamOpenFrame f => $"[server]   STREAM_OPEN {f.Kind} -> {f.Host}:{f.Port}",
                StreamFrame f => $"[server]   STREAM «{Encoding.UTF8.GetString(f.Data.Span).ReplaceLineEndings("\\n")}»",
                _ => $"[server]   {frame}",
            });

        await channel.WriteRecordAsync(BuildAckPacket(0));
    }
}

async Task ProbeTestAsync(KeyPair serverKeys, uint carrier)
{
    Console.WriteLine("\n--- проверка на активный зонд ---");
    var l = new TcpListener(IPAddress.Loopback, 0);
    l.Start();
    int p = ((IPEndPoint)l.LocalEndpoint).Port;

    Task serverSide = Task.Run(async () =>
    {
        using var tcp = await l.AcceptTcpClientAsync();
        try
        {
            await ChameleonHandshake.AcceptAsync(tcp.GetStream(), serverKeys, carrier);
            Console.WriteLine("[server] ОШИБКА: зонд прошёл рукопожатие");
        }
        catch (ChameleonProtocolException)
        {
            Console.WriteLine("[server] зонд отвергнут, соединение закрыто без ответа");
        }
    });

    using (var probe = new TcpClient())
    {
        await probe.ConnectAsync(IPAddress.Loopback, p);
        byte[] junk = new byte[NoiseIkHandshake.Message1Length];
        Random.Shared.NextBytes(junk);
        byte[] framed = [(byte)(junk.Length >> 8), (byte)junk.Length, .. junk];
        await probe.GetStream().WriteAsync(framed);
        await probe.GetStream().FlushAsync();
    }

    await serverSide;
    l.Stop();
}

static byte[] BuildRequestPacket()
{
    byte[] buffer = new byte[512];
    var writer = new PacketWriter(buffer, packetNumber: 0);
    writer.WriteStreamOpen(streamId: 1, StreamKind.Tcp, "example.com", 443);
    writer.WriteStream(streamId: 1, offset: 0, "GET / HTTP/1.1\r\nHost: example.com\r\n\r\n"u8);
    writer.PadTo(512);
    return buffer[..writer.Length];
}

static byte[] BuildAckPacket(ulong acked)
{
    byte[] buffer = new byte[64];
    var writer = new PacketWriter(buffer, packetNumber: 0);
    writer.WriteAck(largestAcked: acked, ackDelayMs: 0, firstRange: 0, ranges: []);
    return buffer[..writer.Length];
}
