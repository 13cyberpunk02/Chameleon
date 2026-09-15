using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Chameleon.Core.Crypto;

public sealed record DirectionKeys(byte[] AeadKey, byte[] MaskKey);

public sealed record CarrierKeys(DirectionKeys Send, DirectionKeys Receive);

/// <summary>
/// Из одного секрета сессии выводит независимые ключи для каждой несущей и направления.
/// Поэтому один и тот же трафик на двух несущих не даёт связанного шифротекста.
/// </summary>
public static class KeySchedule
{
    public const int SecretSize = 32;

    public static CarrierKeys ForCarrier(ReadOnlySpan<byte> sessionSecret, uint carrierId, bool isClient)
    {
        if (sessionSecret.Length != SecretSize)
            throw new ArgumentException($"Секрет должен быть {SecretSize} байт", nameof(sessionSecret));

        var clientToServer = Derive(sessionSecret, "c2s", carrierId);
        var serverToClient = Derive(sessionSecret, "s2c", carrierId);

        return isClient
            ? new CarrierKeys(Send: clientToServer, Receive: serverToClient)
            : new CarrierKeys(Send: serverToClient, Receive: clientToServer);
    }

    private static DirectionKeys Derive(ReadOnlySpan<byte> secret, string direction, uint carrierId) => new(
        AeadKey: Expand(secret, $"chameleon v1 {direction} aead", carrierId),
        MaskKey: Expand(secret, $"chameleon v1 {direction} mask", carrierId));

    private static byte[] Expand(ReadOnlySpan<byte> prk, string label, uint carrierId)
    {
        byte[] labelBytes = Encoding.ASCII.GetBytes(label);
        byte[] info = new byte[labelBytes.Length + 4];
        labelBytes.CopyTo(info, 0);
        BinaryPrimitives.WriteUInt32BigEndian(info.AsSpan(labelBytes.Length), carrierId);

        byte[] key = new byte[32];
        // Секрет из Noise уже равномерно случаен, поэтому используем его как PRK и делаем только Expand.
        HKDF.Expand(HashAlgorithmName.SHA256, prk, key, info);
        return key;
    }
}