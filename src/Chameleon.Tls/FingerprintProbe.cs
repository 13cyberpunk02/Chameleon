using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;

namespace Chameleon.Tls;

/// <summary>
/// Самотест отпечатка: поднимает TLS-клиента <see cref="ChromeTlsClient"/> к
/// локальному SslStream-серверу, перехватывает его ClientHello и считает JA3/JA4.
/// Удобно проверить, что профиль под браузер собран правильно.
/// </summary>
public static class FingerprintProbe
{
    public readonly record struct Result(string Ja3, string Ja4, int ClientHelloBytes);

    public static async Task<Result> MeasureAsync(string sni = "www.example-cdn.com",
        CancellationToken cancellationToken = default)
    {
        using var cert = MakeCert(sni);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var s = await listener.AcceptTcpClientAsync(cancellationToken);
            var ssl = new SslStream(s.GetStream(), false);
            try
            {
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = cert,
                    EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls13,
                }, cancellationToken);
            }
            catch
            {
                // ignored
            }
        }, cancellationToken);

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
        var capture = new CaptureStream(tcp.GetStream());
        var protocol = new TlsClientProtocol(capture);
        try
        {
            protocol.Connect(new ChromeTlsClient(new BcTlsCrypto(new SecureRandom()), sni));
        }
        catch
        {
            // ignored
        }

        await Task.WhenAny(serverTask, Task.Delay(500, cancellationToken));
        listener.Stop();

        byte[] hello = capture.First;
        var (ja3, _) = Ja3.FromClientHelloRecord(hello);
        return new Result(ja3, Ja4.FromClientHelloRecord(hello), hello.Length);
    }

    private static X509Certificate2 MakeCert(string cn)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new System.Security.Cryptography.X509Certificates.CertificateRequest($"CN={cn}", key,
            HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(cn);
        req.CertificateExtensions.Add(san.Build());
        using var c = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        byte[] pfx = c.Export(X509ContentType.Pfx, "x");
#if NET9_0_OR_GREATER
        return X509CertificateLoader.LoadPkcs12(pfx, "x", X509KeyStorageFlags.Exportable);
#else
        return new X509Certificate2(pfx, "x", X509KeyStorageFlags.Exportable);
#endif
    }

    private sealed class CaptureStream(Stream inner) : Stream
    {
        private byte[]? _first;
        public byte[] First => _first ?? [];

        public override void Write(byte[] b, int o, int c)
        {
            _first ??= [.. b.AsSpan(o, c)];
            inner.Write(b, o, c);
        }

        public override int Read(byte[] b, int o, int c) => inner.Read(b, o, c);
        public override void Flush() => inner.Flush();
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
}