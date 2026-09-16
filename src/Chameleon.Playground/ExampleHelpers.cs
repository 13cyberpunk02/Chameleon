using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Chameleon.Playground;

/// <summary>Мелкие вспомогалки для примеров: локальный «сайт», сертификат, in-memory несущая.</summary>
internal static class ExampleHelpers
{
    public static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    /// <summary>Поднимает локальный HTTP-сервер, отвечающий заданным текстом. Возвращает порт.</summary>
    public static int StartHttpOrigin(string message)
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
                ctx.Response.ContentLength64 = body.Length;
                try
                {
                    await ctx.Response.OutputStream.WriteAsync(body);
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

    public static X509Certificate2 MakeSelfSignedCert(string cn)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest($"CN={cn}", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(cn);
        req.CertificateExtensions.Add(san.Build());
        using var c = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        byte[] pfx = c.Export(X509ContentType.Pfx, "x");
        return X509CertificateLoader.LoadPkcs12(pfx, "x", X509KeyStorageFlags.Exportable);
    }
}

/// <summary>In-memory надёжный дуплекс с возможностью «убить» несущую (для примера мультипути).</summary>
internal sealed class DuplexStream : Stream
{
    private readonly PipeReader _reader;
    private readonly PipeWriter _writer;
    public bool Killed { get; private set; }

    private DuplexStream(PipeReader reader, PipeWriter writer)
    {
        _reader = reader;
        _writer = writer;
    }

    public static (DuplexStream Client, DuplexStream Server) CreatePair()
    {
        var c2S = new Pipe();
        var s2C = new Pipe();
        return (new DuplexStream(s2C.Reader, c2S.Writer), new DuplexStream(c2S.Reader, s2C.Writer));
    }

    public void Kill()
    {
        Killed = true;
        try
        {
            _writer.Complete(new IOException("carrier killed"));
            _reader.Complete(new IOException("carrier killed"));
        }
        catch
        {
            // ignored
        }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (Killed) throw new IOException("carrier dead");
        var r = await _reader.ReadAsync(ct);
        if (r.Buffer.IsEmpty && r.IsCompleted) return 0;
        int n = (int)Math.Min(buffer.Length, r.Buffer.Length);
        var slice = r.Buffer.Slice(0, n);
        slice.ToArray().AsSpan().CopyTo(buffer.Span[..n]);
        _reader.AdvanceTo(slice.End);
        return n;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        if (Killed) throw new IOException("carrier dead");
        await _writer.WriteAsync(buffer, ct);
    }

    public override Task FlushAsync(CancellationToken ct) =>
        Killed ? Task.CompletedTask : _writer.FlushAsync(ct).AsTask();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !Killed)
        {
            try
            {
                _writer.Complete();
                _reader.Complete();
            }
            catch
            {
                // ignored
            }
        }
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] b, int o, int c) => ReadAsync(b.AsMemory(o, c)).AsTask().GetAwaiter().GetResult();

    public override void Write(byte[] b, int o, int c) =>
        WriteAsync(b.AsMemory(o, c)).AsTask().GetAwaiter().GetResult();

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => 0;

    public override long Position
    {
        get => 0;
        set { }
    }

    public override long Seek(long o, SeekOrigin s) => 0;

    public override void SetLength(long v)
    {
    }
}