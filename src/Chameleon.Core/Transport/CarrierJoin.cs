using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Chameleon.Core.Transport;

/// <summary>
/// Присоединение новой несущей к уже существующей сессии. Полное рукопожатие
/// Noise не нужно: клиент доказывает знание session_secret через HMAC и называет
/// carrier_id, а ключи record-слоя обе стороны выводят из общего секрета.
///
/// Сообщение join (52 байта, внутри TLS):
///   session_id (16) ‖ carrier_id (4, BE) ‖ nonce (16) ‖ tag (16)
///   tag = HMAC-SHA256(join_key, session_id ‖ carrier_id ‖ nonce)[..16]
/// Ответ сервера (16 байт): HMAC-SHA256(join_key, "ack" ‖ nonce)[..16]
///
/// session_id и join_key выводятся из session_secret, поэтому подделать join без
/// секрета нельзя. Сервер находит сессию по session_id и проверяет tag.
/// </summary>
public static class CarrierJoin
{
    public const int MessageLength = 16 + 4 + 16 + 16; // 52
    public const int AckLength = 16;
    private const int SessionIdLength = 16;
    private const int NonceLength = 16;
    private const int TagLength = 16;

    public readonly record struct JoinRequest(byte[] SessionId, uint CarrierId, byte[] Nonce, byte[] Tag);

    public static byte[] SessionId(ReadOnlySpan<byte> sessionSecret)
        => Expand(sessionSecret, "chameleon v1 carrier join id", SessionIdLength);

    private static byte[] JoinKey(ReadOnlySpan<byte> sessionSecret)
        => Expand(sessionSecret, "chameleon v1 carrier join key", 32);

    public static byte[] BuildJoin(ReadOnlySpan<byte> sessionSecret, uint carrierId)
    {
        byte[] sessionId = SessionId(sessionSecret);
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceLength);

        byte[] message = new byte[MessageLength];
        sessionId.CopyTo(message.AsSpan(0, SessionIdLength));
        BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(SessionIdLength, 4), carrierId);
        nonce.CopyTo(message.AsSpan(SessionIdLength + 4, NonceLength));

        byte[] tag = ComputeTag(sessionSecret, message.AsSpan(0, SessionIdLength + 4 + NonceLength));
        tag.CopyTo(message.AsSpan(SessionIdLength + 4 + NonceLength, TagLength));
        return message;
    }

    public static JoinRequest Parse(ReadOnlySpan<byte> message)
    {
        if (message.Length != MessageLength)
            throw new ChameleonProtocolException("Неверная длина join");
        return new JoinRequest(
            [.. message[..SessionIdLength]],
            BinaryPrimitives.ReadUInt32BigEndian(message.Slice(SessionIdLength, 4)),
            [.. message.Slice(SessionIdLength + 4, NonceLength)],
            [.. message.Slice(SessionIdLength + 4 + NonceLength, TagLength)]);
    }

    /// <summary>Проверяет tag join'а известным секретом сессии (константное время).</summary>
    public static bool Verify(ReadOnlySpan<byte> sessionSecret, in JoinRequest request)
    {
        Span<byte> signed = stackalloc byte[SessionIdLength + 4 + NonceLength];
        request.SessionId.CopyTo(signed[..SessionIdLength]);
        BinaryPrimitives.WriteUInt32BigEndian(signed.Slice(SessionIdLength, 4), request.CarrierId);
        request.Nonce.CopyTo(signed.Slice(SessionIdLength + 4, NonceLength));

        byte[] expected = ComputeTag(sessionSecret, signed);
        return CryptographicOperations.FixedTimeEquals(expected, request.Tag);
    }

    public static byte[] BuildAck(ReadOnlySpan<byte> sessionSecret, ReadOnlySpan<byte> nonce)
    {
        Span<byte> input = stackalloc byte[3 + NonceLength];
        "ack"u8.CopyTo(input);
        nonce.CopyTo(input[3..]);
        using var hmac = new HMACSHA256(JoinKey(sessionSecret));
        return hmac.ComputeHash([.. input])[..AckLength];
    }

    public static bool VerifyAck(ReadOnlySpan<byte> sessionSecret, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ack)
        => CryptographicOperations.FixedTimeEquals(BuildAck(sessionSecret, nonce), ack);

    private static byte[] ComputeTag(ReadOnlySpan<byte> sessionSecret, ReadOnlySpan<byte> data)
    {
        using var hmac = new HMACSHA256(JoinKey(sessionSecret));
        return hmac.ComputeHash(data.ToArray())[..TagLength];
    }

    private static byte[] Expand(ReadOnlySpan<byte> secret, string label, int length)
    {
        byte[] output = new byte[length];
        HKDF.Expand(HashAlgorithmName.SHA256, secret, output, Encoding.ASCII.GetBytes(label));
        return output;
    }
}