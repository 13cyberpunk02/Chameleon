using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Chameleon.Core.Transport;

/// <summary>
/// Оборачивает подключённый сокет в поток-несущую. Возвращаемый Stream владеет
/// сокетом: его Dispose закрывает соединение целиком.
/// </summary>
public delegate Task<Stream> CarrierWrapper(Socket socket, CancellationToken cancellationToken);

/// <summary>
/// TLS-несущая: настоящее TLS-соединение, внутри которого едет протокол Chameleon.
/// Снаружи это обычный HTTPS.
///
/// Важное ограничение: ClientHello здесь формирует System.Net.Security.SslStream
/// (SChannel на Windows, OpenSSL на Linux). Это валидный современный TLS 1.3, но
/// по отпечатку JA3/JA4 он НЕ совпадает с браузером - в .NET нет управления
/// порядком расширений и GREASE. Мимикрия под конкретный браузер - отдельный
/// подэтап (аналог uTLS), см. заметку в SPEC.
/// </summary>
public static class TlsCarrier
{
    private static readonly List<SslApplicationProtocol> BrowserAlpn =
        [SslApplicationProtocol.Http2, SslApplicationProtocol.Http11];

    /// <summary>
    /// Клиентская несущая. <paramref name="serverName"/> - SNI (в бою это домен
    /// прикрытия). <paramref name="validate"/> по умолчанию принимает любой
    /// сертификат (для самоподписанных в тестах); в бою сюда ставится пиннинг.
    /// </summary>
    public static CarrierWrapper Client(
        string serverName,
        RemoteCertificateValidationCallback? validate = null,
        Action<SslStream>? onHandshake = null)
        => async (socket, cancellationToken) =>
        {
            var network = new NetworkStream(socket, ownsSocket: true);
            var tls = new SslStream(network, leaveInnerStreamOpen: false, validate ?? AcceptAny);

            var options = new SslClientAuthenticationOptions
            {
                TargetHost = serverName,
                ApplicationProtocols = BrowserAlpn,
                EnabledSslProtocols = SslProtocols.Tls13 | SslProtocols.Tls12,
            };

            await tls.AuthenticateAsClientAsync(options, cancellationToken).ConfigureAwait(false);
            onHandshake?.Invoke(tls);
            return tls;
        };

    /// <summary>Серверная несущая: предъявляет сертификат и терминирует TLS.</summary>
    public static CarrierWrapper Server(X509Certificate2 certificate)
        => async (socket, cancellationToken) =>
        {
            var network = new NetworkStream(socket, ownsSocket: true);
            var tls = new SslStream(network, leaveInnerStreamOpen: false);

            var options = new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate,
                ApplicationProtocols = BrowserAlpn,
                EnabledSslProtocols = SslProtocols.Tls13 | SslProtocols.Tls12,
            };

            await tls.AuthenticateAsServerAsync(options, cancellationToken).ConfigureAwait(false);
            return tls;
        };

    /// <summary>Самоподписанный ECDSA-сертификат (P-256) для локальных тестов.</summary>
    public static X509Certificate2 CreateSelfSignedCertificate(string commonName)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={commonName}", key, HashAlgorithmName.SHA256);

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(commonName);
        request.CertificateExtensions.Add(san.Build());

        using X509Certificate2 certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

        byte[] pfx = certificate.Export(X509ContentType.Pfx);

        return X509CertificateLoader.LoadPkcs12(pfx, password: null, X509KeyStorageFlags.Exportable);
    }

    private static bool AcceptAny(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
        => true;
}