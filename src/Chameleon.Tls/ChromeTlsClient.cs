using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto;

namespace Chameleon.Tls;

/// <summary>
/// TLS-клиент BouncyCastle с ClientHello «под браузер»: мы задаём список
/// cipher suites, групп, ALPN и версий, поэтому отпечаток JA3/JA4 контролируем мы,
/// а не системный SslStream.
/// </summary>
public sealed class ChromeTlsClient(TlsCrypto crypto, string sni) : DefaultTlsClient(crypto)
{
    // Порядок и состав - как у современного Chrome (TLS 1.3 наборы впереди).
    public override int[] GetCipherSuites() =>
    [
        CipherSuite.TLS_AES_128_GCM_SHA256,
        CipherSuite.TLS_AES_256_GCM_SHA384,
        CipherSuite.TLS_CHACHA20_POLY1305_SHA256,
        CipherSuite.TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256,
        CipherSuite.TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256,
    ];

    public override ProtocolVersion[] GetProtocolVersions() =>
        [ProtocolVersion.TLSv13, ProtocolVersion.TLSv12];

    protected override IList<int> GetSupportedGroups(IList<int> namedGroupRoles) =>
        [NamedGroup.x25519, NamedGroup.secp256r1, NamedGroup.secp384r1];

    protected override IList<ProtocolName> GetProtocolNames() =>
        [ProtocolName.Http_2_Tls, ProtocolName.Http_1_1]; // ALPN: h2, http/1.1

    protected override IList<ServerName> GetSniServerNames() =>
        [new ServerName(NameType.host_name, Org.BouncyCastle.Utilities.Strings.ToUtf8ByteArray(sni))];

    public override TlsAuthentication GetAuthentication() => new AcceptAllAuthentication();

    private sealed class AcceptAllAuthentication : TlsAuthentication
    {
        // Прототип: сертификат сервера не проверяем (в бою - пиннинг).
        public void NotifyServerCertificate(TlsServerCertificate serverCertificate) { }
        public TlsCredentials? GetClientCredentials(CertificateRequest certificateRequest) => null;
    }
}