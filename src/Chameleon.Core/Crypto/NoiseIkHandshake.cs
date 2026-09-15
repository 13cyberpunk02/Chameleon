namespace Chameleon.Core.Crypto;

/// <summary>
/// Результат рукопожатия: общий секрет сессии и статический ключ собеседника.
/// </summary>
public sealed record HandshakeResult(byte[] SessionSecret, byte[] RemoteStaticPublic);

/// <summary>
/// Рукопожатие Noise_IK_25519_ChaChaPoly_SHA256.
///
///   pre-message: responder static (клиент знает публичный ключ сервера заранее)
///   msg1 (client → server): e, es, s, ss
///   msg2 (server → client): e, ee, se
///
/// Что это даёт:
///  • клиент аутентифицирует сервер по заранее известному ключу - активный зонд
///    цензора, не знающий его, не пройдёт;
///  • статический ключ клиента передаётся уже зашифрованным (токен s внутри AEAD);
///  • forward secrecy за счёт эфемерных ключей с обеих сторон.
///
/// Payload в msg1 держим пустым: сообщение может быть переиграно (replay) на сервер,
/// поэтому полезные данные отправляются только после msg2, по установленным ключам.
/// </summary>
public sealed class NoiseIkHandshake
{
    private const string ProtocolName = "Noise_IK_25519_ChaChaPoly_SHA256";
    private static readonly byte[] Prologue = "chameleon v1"u8.ToArray();

    private const int DhLen = 32;
    private const int TagLen = 16;

    // Длины сообщений на проводе при пустом payload:
    public const int Message1Length = DhLen + (DhLen + TagLen) + TagLen; // e ‖ enc(s) ‖ enc(∅) = 96
    public const int Message2Length = DhLen + TagLen; // e ‖ enc(∅)      = 48

    private readonly SymmetricState _symmetric = new();
    private readonly bool _initiator;
    private readonly KeyPair _staticKeys;

    private KeyPair _ephemeralKeys;
    private byte[]? _remoteStatic;
    private byte[]? _remoteEphemeral;

    private NoiseIkHandshake(bool initiator, KeyPair staticKeys, byte[]? remoteStatic)
    {
        _initiator = initiator;
        _staticKeys = staticKeys;

        _symmetric.Initialize(ProtocolName);
        _symmetric.MixHash(Prologue);

        // Обработка pre-message: обе стороны подмешивают статический ключ сервера.
        byte[] serverStatic = initiator
            ? remoteStatic ?? throw new ArgumentNullException(nameof(remoteStatic))
            : staticKeys.Public;
        _symmetric.MixHash(serverStatic);

        _remoteStatic = initiator ? remoteStatic : null;
    }

    public static NoiseIkHandshake CreateInitiator(KeyPair clientStatic, byte[] serverStaticPublic)
        => new(initiator: true, clientStatic, serverStaticPublic);

    public static NoiseIkHandshake CreateResponder(KeyPair serverStatic)
        => new(initiator: false, serverStatic, remoteStatic: null);

    /// <summary>Клиент формирует msg1.</summary>
    public byte[] WriteMessage1()
    {
        EnsureInitiator(true);

        _ephemeralKeys = X25519.GenerateKeyPair();
        var writer = new MemoryStream();

        _symmetric.MixHash(_ephemeralKeys.Public);
        writer.Write(_ephemeralKeys.Public);

        _symmetric.MixKey(Dh(_ephemeralKeys.Private, _remoteStatic!)); // es
        writer.Write(_symmetric.EncryptAndHash(_staticKeys.Public)); // s (зашифрован)
        _symmetric.MixKey(Dh(_staticKeys.Private, _remoteStatic!)); // ss
        writer.Write(_symmetric.EncryptAndHash([])); // payload = ∅

        return writer.ToArray();
    }

    /// <summary>Сервер разбирает msg1 и узнаёт статический ключ клиента.</summary>
    public void ReadMessage1(ReadOnlySpan<byte> message)
    {
        EnsureInitiator(false);
        if (message.Length != Message1Length)
            throw new ChameleonProtocolException("Неверная длина msg1");

        _remoteEphemeral = message[..DhLen].ToArray();
        _symmetric.MixHash(_remoteEphemeral);

        _symmetric.MixKey(Dh(_staticKeys.Private, _remoteEphemeral)); // es
        _remoteStatic = _symmetric.DecryptAndHash(message.Slice(DhLen, DhLen + TagLen));
        _symmetric.MixKey(Dh(_staticKeys.Private, _remoteStatic)); // ss
        _symmetric.DecryptAndHash(message[(DhLen + DhLen + TagLen)..]); // payload
    }

    /// <summary>Сервер формирует msg2 и завершает рукопожатие.</summary>
    public HandshakeResult WriteMessage2(out byte[] message)
    {
        EnsureInitiator(false);

        _ephemeralKeys = X25519.GenerateKeyPair();
        var writer = new MemoryStream();

        _symmetric.MixHash(_ephemeralKeys.Public);
        writer.Write(_ephemeralKeys.Public);

        _symmetric.MixKey(Dh(_ephemeralKeys.Private, _remoteEphemeral!)); // ee
        _symmetric.MixKey(Dh(_ephemeralKeys.Private, _remoteStatic!)); // se
        writer.Write(_symmetric.EncryptAndHash([])); // payload = ∅

        message = writer.ToArray();
        return Finish();
    }

    /// <summary>Клиент разбирает msg2 и завершает рукопожатие.</summary>
    public HandshakeResult ReadMessage2(ReadOnlySpan<byte> message)
    {
        EnsureInitiator(true);
        if (message.Length != Message2Length)
            throw new ChameleonProtocolException("Неверная длина msg2");

        _remoteEphemeral = message[..DhLen].ToArray();
        _symmetric.MixHash(_remoteEphemeral);

        _symmetric.MixKey(Dh(_ephemeralKeys.Private, _remoteEphemeral)); // ee
        _symmetric.MixKey(Dh(_staticKeys.Private, _remoteEphemeral)); // se
        _symmetric.DecryptAndHash(message[DhLen..]); // payload

        return Finish();
    }

    private HandshakeResult Finish()
    {
        byte[] sessionSecret = _symmetric.DeriveSecret("chameleon v1 session secret");
        return new HandshakeResult(sessionSecret, _remoteStatic!);
    }

    private static byte[] Dh(ReadOnlySpan<byte> privateKey, ReadOnlySpan<byte> publicKey)
        => X25519.ScalarMult(privateKey, publicKey);

    private void EnsureInitiator(bool expected)
    {
        if (_initiator != expected)
            throw new InvalidOperationException("Неверная роль для этого шага рукопожатия");
    }
}