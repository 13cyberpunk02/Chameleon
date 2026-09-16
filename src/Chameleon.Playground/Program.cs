using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Chameleon.Core.Crypto;
using Chameleon.Core.Proxy;
using Chameleon.Core.Session;
using Chameleon.Core.Transport;

// Ревизия 3-2: стохастический шейпер-автомат.
// (1) тайминги прикрытия рандомизированы и зависят от режима (нет «метронома»);
// (2) режимы переключаются вероятностно; (3) модель можно сменить на лету;
// (4) реальные данные по-прежнему квантуются, прокси работает.

Console.WriteLine("=== (1-2) выборка автомата WebBrowsing: паузы, размеры, режимы ===");
var shaper = TrafficShaper.WebBrowsing;
var delays = new List<int>();
var coverSizes = new HashSet<int>();
var regimes = new List<string>();
var sw = Stopwatch.StartNew();

while (sw.Elapsed < TimeSpan.FromSeconds(12) && regimes.Distinct().Count() < 2)
{
    var (delay, size) = shaper.NextCover();
    delays.Add((int)delay.TotalMilliseconds);
    coverSizes.Add(size);
    string regime = shaper.CurrentRegimeName;
    if (regimes.Count == 0 || regimes[^1] != regime) regimes.Add(regime);
    await Task.Delay(delay);
}

sw.Stop();
Console.WriteLine(
    $"паузы, мс: min={delays.Min()} max={delays.Max()} уникальных={delays.Distinct().Count()} (значит не метроном)");
Console.WriteLine($"размеры прикрытия: [{string.Join(", ", coverSizes.OrderBy(x => x))}] (все со ступеней лесенки)");
Console.WriteLine($"последовательность режимов за {sw.Elapsed.TotalSeconds:0.0} с: {string.Join(" → ", regimes)}");

Console.WriteLine("\n=== (3) смена модели на лету: WebBrowsing → Streaming ===");
Console.WriteLine($"до:    модель={shaper.CurrentModelName}");
shaper.SwitchModel(TrafficModel.Streaming);
var streamingSizes = new HashSet<int>();
for (int i = 0; i < 30; i++)
{
    streamingSizes.Add(shaper.NextCover().Size);
    await Task.Delay(10);
}

Console.WriteLine(
    $"после: модель={shaper.CurrentModelName}, размеры прикрытия=[{string.Join(", ", streamingSizes.OrderBy(x => x))}] (крупнее - это видео)");

Console.WriteLine("\n=== (4) прокси с шейпером всё ещё работает, данные квантуются ===");
await ProxyStillWorks();

static async Task ProxyStillWorks()
{
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
    int originPort = StartHttp(new string('X', 3000));
    var sizes = new ConcurrentQueue<int>();

    KeyPair serverStatic = X25519.GenerateKeyPair();
    KeyPair clientStatic = X25519.GenerateKeyPair();

    var model = TrafficShaper.WebBrowsing;
    await using var server = ChameleonServer.Start(new IPEndPoint(IPAddress.Loopback, 0), serverStatic,
        shaper: TrafficShaper.WebBrowsing);
    CarrierWrapper counting = (socket, ct) =>
        Task.FromResult<Stream>(new CountingStream(new NetworkStream(socket, ownsSocket: true), sizes));

    await using var client = await ChameleonClient.StartAsync(
        server.EndPoint, clientStatic, serverStatic.Public,
        new IPEndPoint(IPAddress.Loopback, 0), carrier: counting, shaper: model, cancellationToken: cts.Token);
    sizes.Clear();

    using var http = new HttpClient(new SocketsHttpHandler
    {
        Proxy = new WebProxy($"socks5://127.0.0.1:{client.SocksEndPoint.Port}"),
        UseProxy = true,
    });
    string body = await http.GetStringAsync($"http://127.0.0.1:{originPort}/", cts.Token);

    var ladder = new[] { 128, 512, 1536, 4096, RecordFormat.MaxPlaintext };
    bool allQuantized = sizes.All(s => ladder.Contains(s - RecordFormat.Overhead));
    Console.WriteLine(
        $"прокси отдал {body.Length} байт; размеры на проводе на ступенях лесенки: {(allQuantized ? "✓ да" : "✗ нет")}");
}

static int StartHttp(string message)
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