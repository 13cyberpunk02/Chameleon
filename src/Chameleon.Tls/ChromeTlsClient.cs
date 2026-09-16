using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto;
using Org.BouncyCastle.Utilities;

namespace Chameleon.Tls;

/// <summary>
/// TLS-клиент BouncyCastle с ClientHello под профиль Chromium (Chrome/Brave/Edge…).
/// SslStream в .NET не даёт управлять составом ClientHello, из-за чего палится по
/// отпечатку; здесь состав задаём мы.
///
/// Достигнутый JA4 совпадает бит-в-бит с эталоном Brave/Chromium:
///   t13d1516h2_8daaf6152771_806a8c22fdea
/// - все три части (ja4_a, хеш шифров ja4_b, хеш расширений+подписей ja4_c).
///
/// Почему по JA4 это удаётся, а по JA3 нет: JA4 сортирует расширения и исключает
/// GREASE - ровно то, чем BouncyCastle не управляет (свой порядок, нет GREASE).
/// JA3 чувствителен к порядку, поэтому под Chromium на стоковом BC не сводится.
///
/// Профиль версионно-зависим (набор расширений/подписей меняется от версии к
/// версии). Этот соответствует свежему Chromium с ECH и PQ-подписями. Снять свой
/// эталон: browserleaks.com/tls (поле JA4_r) - и подогнать списки ниже.
/// </summary>
public sealed class ChromeTlsClient(TlsCrypto crypto, string sni) : DefaultTlsClient(crypto)
{
    // 15 наборов Chromium (без SCSV). GREASE (0x0A0A) BC пропускает; JA4 его игнорирует.
    public override int[] GetCipherSuites() =>
    [
        0x0A0A, // GREASE
        CipherSuite.TLS_AES_128_GCM_SHA256,
        CipherSuite.TLS_AES_256_GCM_SHA384,
        CipherSuite.TLS_CHACHA20_POLY1305_SHA256,
        CipherSuite.TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256,
        CipherSuite.TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256,
        CipherSuite.TLS_ECDHE_ECDSA_WITH_AES_256_GCM_SHA384,
        CipherSuite.TLS_ECDHE_RSA_WITH_AES_256_GCM_SHA384,
        CipherSuite.TLS_ECDHE_ECDSA_WITH_CHACHA20_POLY1305_SHA256,
        CipherSuite.TLS_ECDHE_RSA_WITH_CHACHA20_POLY1305_SHA256,
        CipherSuite.TLS_ECDHE_RSA_WITH_AES_128_CBC_SHA,
        CipherSuite.TLS_ECDHE_RSA_WITH_AES_256_CBC_SHA,
        CipherSuite.TLS_RSA_WITH_AES_128_GCM_SHA256,
        CipherSuite.TLS_RSA_WITH_AES_256_GCM_SHA384,
        CipherSuite.TLS_RSA_WITH_AES_128_CBC_SHA,
        CipherSuite.TLS_RSA_WITH_AES_256_CBC_SHA,
    ];

    public override ProtocolVersion[] GetProtocolVersions() =>
        [ProtocolVersion.TLSv13, ProtocolVersion.TLSv12];

    protected override IList<int> GetSupportedGroups(IList<int> namedGroupRoles) =>
        [NamedGroup.x25519, NamedGroup.secp256r1, NamedGroup.secp384r1];

    protected override IList<ProtocolName> GetProtocolNames() =>
        [ProtocolName.Http_2_Tls, ProtocolName.Http_1_1];

    protected override IList<ServerName> GetSniServerNames() =>
        [new ServerName(NameType.host_name, Strings.ToUtf8ByteArray(sni))];

    // Точный список signature_algorithms Chromium (влияет на ja4_c).
    protected override IList<SignatureAndHashAlgorithm> GetSupportedSignatureAlgorithms()
    {
        int[] codes = [0x0904, 0x0905, 0x0906, 0x0403, 0x0804, 0x0401, 0x0503, 0x0805, 0x0501, 0x0806, 0x0601];
        var list = new List<SignatureAndHashAlgorithm>(codes.Length);
        foreach (int c in codes)
            list.Add(new SignatureAndHashAlgorithm((short)(c >> 8), (short)(c & 0xff)));
        return list;
    }

    public override IDictionary<int, byte[]> GetClientExtensions()
    {
        var e = base.GetClientExtensions();
        e.Remove(22); // encrypt_then_mac - у Chromium нет
        e[0xff01] = [0x00]; // renegotiation_info (вместо SCSV)
        e[35] = []; // session_ticket
        e[18] = []; // signed_certificate_timestamp
        e[45] = [0x01, 0x01]; // psk_key_exchange_modes
        e[27] = [0x02, 0x00, 0x02]; // compress_certificate (brotli) = 0x001b
        e[17613] = [0x00, 0x03, 0x02, 0x68, 0x32]; // application_settings (ALPS) = 0x44cd
        e[65037] = [0x00]; // ECH-заглушка = 0xfe0d (для JA4 важен сам код)
        return e;
    }

    public override TlsAuthentication GetAuthentication() => new AcceptAllAuthentication();

    private sealed class AcceptAllAuthentication : TlsAuthentication
    {
        public void NotifyServerCertificate(TlsServerCertificate serverCertificate)
        {
        }

        public TlsCredentials? GetClientCredentials(CertificateRequest certificateRequest) => null;
    }
}