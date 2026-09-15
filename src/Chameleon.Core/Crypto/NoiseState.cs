using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Chameleon.Core.Crypto;

/// <summary>
/// CipherState из Noise Protocol Framework (revision 34). Nonce - 96 бит,
/// первые 32 нулевые, младшие 64 - счётчик в little-endian (в отличие от нашего
/// record-слоя, где counter big-endian; это разные подсистемы).
/// </summary>
internal sealed class CipherState
{
    private byte[]? _key;
    private ulong _nonce;

    public bool HasKey => _key is not null;

    public void InitializeKey(byte[]? key)
    {
        _key = key;
        _nonce = 0;
    }

    public byte[] EncryptWithAd(ReadOnlySpan<byte> associatedData, ReadOnlySpan<byte> plaintext)
    {
        if (_key is null) return plaintext.ToArray();

        byte[] output = new byte[plaintext.Length + 16];
        var ciphertext = output.AsSpan(0, plaintext.Length);
        var tag = output.AsSpan(plaintext.Length, 16);

        Span<byte> nonce = stackalloc byte[12];
        WriteNonce(nonce, _nonce);
        using var aead = new ChaCha20Poly1305(_key);
        aead.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);

        _nonce++;
        return output;
    }

    public byte[] DecryptWithAd(ReadOnlySpan<byte> associatedData, ReadOnlySpan<byte> ciphertext)
    {
        if (_key is null) return ciphertext.ToArray();
        if (ciphertext.Length < 16)
            throw new ChameleonProtocolException("Слишком короткий шифротекст в рукопожатии");

        int plaintextLength = ciphertext.Length - 16;
        byte[] plaintext = new byte[plaintextLength];

        Span<byte> nonce = stackalloc byte[12];
        WriteNonce(nonce, _nonce);
        using var aead = new ChaCha20Poly1305(_key);
        try
        {
            aead.Decrypt(nonce, ciphertext[..plaintextLength], ciphertext[plaintextLength..], plaintext,
                associatedData);
        }
        catch (CryptographicException)
        {
            throw new ChameleonProtocolException("Аутентификация в рукопожатии не прошла");
        }

        _nonce++;
        return plaintext;
    }

    private static void WriteNonce(Span<byte> nonce, ulong counter)
    {
        nonce[..4].Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(nonce[4..], counter);
    }
}

/// <summary>SymmetricState из Noise: хранит chaining key (ck) и накопленный хеш (h).</summary>
internal sealed class SymmetricState
{
    private readonly CipherState _cipher = new();
    private byte[] _chainingKey = null!;
    private byte[] _hash = null!;

    public byte[] ChainingKey => _chainingKey;
    public byte[] Hash => _hash;

    public void Initialize(string protocolName)
    {
        byte[] name = Encoding.ASCII.GetBytes(protocolName);
        _hash = new byte[32];
        if (name.Length <= 32)
            name.CopyTo(_hash, 0); // короче хеша - дополняем нулями
        else
            _hash = SHA256.HashData(name);

        _chainingKey = (byte[])_hash.Clone();
        _cipher.InitializeKey(null);
    }

    public void MixKey(ReadOnlySpan<byte> inputKeyMaterial)
    {
        var (ck, tempKey) = Hkdf2(_chainingKey, inputKeyMaterial);
        _chainingKey = ck;
        _cipher.InitializeKey(tempKey);
    }

    public void MixHash(ReadOnlySpan<byte> data)
    {
        byte[] combined = new byte[_hash.Length + data.Length];
        _hash.CopyTo(combined, 0);
        data.CopyTo(combined.AsSpan(_hash.Length));
        _hash = SHA256.HashData(combined);
    }

    public byte[] EncryptAndHash(ReadOnlySpan<byte> plaintext)
    {
        byte[] ciphertext = _cipher.EncryptWithAd(_hash, plaintext);
        MixHash(ciphertext);
        return ciphertext;
    }

    public byte[] DecryptAndHash(ReadOnlySpan<byte> ciphertext)
    {
        byte[] plaintext = _cipher.DecryptWithAd(_hash, ciphertext);
        MixHash(ciphertext);
        return plaintext;
    }

    /// <summary>Выводит из ck дополнительный секрет с доменным разделением по метке.</summary>
    public byte[] DeriveSecret(string label)
    {
        using var hmac = new HMACSHA256(_chainingKey);
        return hmac.ComputeHash(Encoding.ASCII.GetBytes(label));
    }

    private static (byte[], byte[]) Hkdf2(byte[] chainingKey, ReadOnlySpan<byte> inputKeyMaterial)
    {
        byte[] tempKey = HmacSha256(chainingKey, inputKeyMaterial);
        byte[] output1 = HmacSha256(tempKey, [0x01]);
        byte[] output2 = HmacSha256(tempKey, [.. output1, 0x02]);
        return (output1, output2);
    }

    private static byte[] HmacSha256(byte[] key, ReadOnlySpan<byte> data)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(data.ToArray());
    }
}