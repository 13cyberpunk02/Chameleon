using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Chameleon.Core.Crypto;
using Chameleon.Core.Proxy;
using Chameleon.Core.Session;
using Chameleon.Core.Transport;

// Проверка шейпера: сравниваем размеры record'ов на проводе БЕЗ и С шейпингом.
// Несущая - голый TCP с обёрткой-счётчиком (чтобы видеть именно наши record'ы,
// без повторной нарезки в TLS). Функционально прокси работает в обоих случаях.

int originPort = StartHttp("origin", new string('X', 3000));

Console.WriteLine("=== БЕЗ шейпера ===");
await RunOnce(TrafficShaper.Off, originPort, coverWaitMs: 0);

Console.WriteLine("\n=== С шейпером (WebBrowsing) ===");
await RunOnce(TrafficShaper.WebBrowsing, originPort, coverWaitMs: 700);

static async Task RunOnce(TrafficShaper shaper, int originPort, int coverWaitMs)
{
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
    var sizes = new ConcurrentQueue<int>();

    KeyPair serverStatic = X25519.GenerateKeyPair();
    KeyPair clientStatic = X25519.GenerateKeyPair();

    await using var server = ChameleonServer.Start(
        new IPEndPoint(IPAddress.Loopback, 0), serverStatic, shaper: shaper);

    // Клиентская несущая - TCP + счётчик размеров исходящих record'ов.
    CarrierWrapper counting = (socket, ct) =>
        Task.FromResult<Stream>(new CountingStream(new NetworkStream(socket, ownsSocket: true), sizes));

    await using var client = await ChameleonClient.StartAsync(
        server.EndPoint, clientStatic, serverStatic.Public,
        new IPEndPoint(IPAddress.Loopback, 0), carrier: counting, shaper: shaper, cancellationToken: cts.Token);

    sizes.Clear();

    var handler = new SocketsHttpHandler
    {
        Proxy = new WebProxy($"socks5://127.0.0.1:{client.SocksEndPoint.Port}"),
        UseProxy = true,
    };
    using var http = new HttpClient(handler);
    string body = await http.GetStringAsync($"http://127.0.0.1:{originPort}/", cts.Token);
    Console.WriteLine($"прокси отдал {body.Length} байт тела - работает");

    if (coverWaitMs > 0)
    {
        int before = sizes.Count;
        await Task.Delay(coverWaitMs, cts.Token);
        Console.WriteLine($"за {coverWaitMs} мс простоя добавилось пакетов-прикрытия: {sizes.Count - before}");
    }

    var distinct = sizes.OrderBy(x => x).Distinct().ToList();
    Console.WriteLine($"размеры record'ов на проводе ({sizes.Count} шт), уникальные: [{string.Join(", ", distinct)}]");

    if (shaper.Enabled)
    {
        var ladder = new[] { 128, 512, 1536, 4096, RecordFormat.MaxPlaintext };
        // на проводе record = plaintext + 18 байт (2 заголовок + 16 тег)
        bool allQuantized = sizes.All(s => ladder.Contains(s - RecordFormat.Overhead));
        Console.WriteLine(allQuantized
            ? "✓ все размеры совпадают со ступенями лесенки (настоящие длины скрыты)"
            : "✗ есть размеры вне лесенки");
    }
}

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

            byte[] b = Encoding.UTF8.GetBytes(message);
            ctx.Response.ContentLength64 = b.Length;
            try
            {
                await ctx.Response.OutputStream.WriteAsync(b);
                ctx.Response.Close();
            }
            catch
            {
                // ignored
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

// Обёртка, считающая размеры каждого записанного record'а (одна запись = один record).
sealed class CountingStream(Stream inner, ConcurrentQueue<int> sizes) : Stream
{
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        sizes.Enqueue(buffer.Length);
        await inner.WriteAsync(buffer, cancellationToken);
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => inner.ReadAsync(buffer, cancellationToken);

    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync();
        await base.DisposeAsync();
    }

    public override void Flush() => inner.Flush();
    public override int Read(byte[] b, int o, int c) => inner.Read(b, o, c);

    public override void Write(byte[] b, int o, int c)
    {
        sizes.Enqueue(c);
        inner.Write(b, o, c);
    }

    public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
    public override void SetLength(long v) => throw new NotSupportedException();
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }
}