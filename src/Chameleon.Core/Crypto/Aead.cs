using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;

namespace Chameleon.Core.Crypto;

/// <summary>
/// ChaCha20-Poly1305 с единым API. Если ОС поддерживает нативную реализацию
/// (Windows 11, Linux/OpenSSL) - используется она, ради скорости. Иначе
/// (например Windows 10) - переносимая реализация на чистом C# по RFC 8439.
/// Сигнатуры повторяют System.Security.Cryptography.ChaCha20Poly1305.
/// </summary>
public sealed class Aead : IDisposable
{
    public const int KeySize = 32;
    public const int NonceSize = 12;
    public const int TagSize = 16;

    private readonly ChaCha20Poly1305? _native;
    private readonly byte[]? _key;

    public Aead(ReadOnlySpan<byte> key)
    {
        if (key.Length != KeySize) throw new ArgumentException("Ключ должен быть 32 байта", nameof(key));

        if (ChaCha20Poly1305.IsSupported)
            _native = new ChaCha20Poly1305(key);
        else
            _key = key.ToArray();
    }

    public static bool UsesNativeImplementation => ChaCha20Poly1305.IsSupported;

    public void Encrypt(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> plaintext,
        Span<byte> ciphertext, Span<byte> tag, ReadOnlySpan<byte> associatedData = default)
    {
        if (_native is not null)
            _native.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
        else
            ManagedChaCha20Poly1305.Encrypt(_key!, nonce, plaintext, ciphertext, tag, associatedData);
    }

    /// <exception cref="CryptographicException">Тег не совпал.</exception>
    public void Decrypt(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ciphertext,
        ReadOnlySpan<byte> tag, Span<byte> plaintext, ReadOnlySpan<byte> associatedData = default)
    {
        if (_native is not null)
            _native.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);
        else
            ManagedChaCha20Poly1305.Decrypt(_key!, nonce, ciphertext, tag, plaintext, associatedData);
    }

    public void Dispose() => _native?.Dispose();
}

/// <summary>
/// Реализация ChaCha20-Poly1305 (RFC 8439) на чистом C#. Poly1305 считается
/// через BigInteger: корректно и переносимо, но не константного времени и не
/// самое быстрое. Годится как фолбэк; на «горячем» пути ОС отдаёт нативную версию.
/// </summary>
internal static class ManagedChaCha20Poly1305
{
    public static void Encrypt(byte[] key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> plaintext,
        Span<byte> ciphertext, Span<byte> tag, ReadOnlySpan<byte> associatedData)
    {
        Span<byte> otk = stackalloc byte[32];
        Poly1305KeyGen(key, nonce, otk);

        ChaCha20Xor(key, counter: 1, nonce, plaintext, ciphertext);

        Span<byte> computed = stackalloc byte[16];
        Poly1305Mac(associatedData, ciphertext, otk, computed);
        computed.CopyTo(tag);
    }

    public static void Decrypt(byte[] key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ciphertext,
        ReadOnlySpan<byte> tag, Span<byte> plaintext, ReadOnlySpan<byte> associatedData)
    {
        Span<byte> otk = stackalloc byte[32];
        Poly1305KeyGen(key, nonce, otk);

        Span<byte> computed = stackalloc byte[16];
        Poly1305Mac(associatedData, ciphertext, otk, computed);

        if (!CryptographicOperations.FixedTimeEquals(computed, tag))
            throw new CryptographicException("Poly1305: тег не совпал");

        ChaCha20Xor(key, counter: 1, nonce, ciphertext, plaintext);
    }

    private static void Poly1305KeyGen(byte[] key, ReadOnlySpan<byte> nonce, Span<byte> output)
    {
        Span<byte> block = stackalloc byte[64];
        ChaChaBlock(InitState(key, counter: 0, nonce), block);
        block[..32].CopyTo(output);
    }

    private static uint[] InitState(byte[] key, uint counter, ReadOnlySpan<byte> nonce)
    {
        uint[] s = new uint[16];
        s[0] = 0x61707865;
        s[1] = 0x3320646e;
        s[2] = 0x79622d32;
        s[3] = 0x6b206574;
        for (int i = 0; i < 8; i++) s[4 + i] = BinaryPrimitives.ReadUInt32LittleEndian(key.AsSpan(i * 4));
        s[12] = counter;
        s[13] = BinaryPrimitives.ReadUInt32LittleEndian(nonce[..4]);
        s[14] = BinaryPrimitives.ReadUInt32LittleEndian(nonce.Slice(4, 4));
        s[15] = BinaryPrimitives.ReadUInt32LittleEndian(nonce.Slice(8, 4));
        return s;
    }

    private static void ChaCha20Xor(byte[] key, uint counter, ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> input, Span<byte> output)
    {
        uint[] state = InitState(key, counter, nonce);
        Span<byte> keystream = stackalloc byte[64];

        int offset = 0;
        while (offset < input.Length)
        {
            ChaChaBlock(state, keystream);
            int n = Math.Min(64, input.Length - offset);
            for (int i = 0; i < n; i++)
                output[offset + i] = (byte)(input[offset + i] ^ keystream[i]);
            state[12]++;
            offset += n;
        }
    }

    private static void ChaChaBlock(uint[] state, Span<byte> output)
    {
        Span<uint> w = stackalloc uint[16];
        state.CopyTo(w);

        for (int i = 0; i < 10; i++)
        {
            QuarterRound(w, 0, 4, 8, 12);
            QuarterRound(w, 1, 5, 9, 13);
            QuarterRound(w, 2, 6, 10, 14);
            QuarterRound(w, 3, 7, 11, 15);
            QuarterRound(w, 0, 5, 10, 15);
            QuarterRound(w, 1, 6, 11, 12);
            QuarterRound(w, 2, 7, 8, 13);
            QuarterRound(w, 3, 4, 9, 14);
        }

        for (int i = 0; i < 16; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(output.Slice(i * 4, 4), w[i] + state[i]);
    }

    private static void QuarterRound(Span<uint> w, int a, int b, int c, int d)
    {
        w[a] += w[b];
        w[d] ^= w[a];
        w[d] = Rotl(w[d], 16);
        w[c] += w[d];
        w[b] ^= w[c];
        w[b] = Rotl(w[b], 12);
        w[a] += w[b];
        w[d] ^= w[a];
        w[d] = Rotl(w[d], 8);
        w[c] += w[d];
        w[b] ^= w[c];
        w[b] = Rotl(w[b], 7);
    }

    private static uint Rotl(uint x, int n) => (x << n) | (x >> (32 - n));

    private static readonly BigInteger Prime = (BigInteger.One << 130) - 5;

    private static readonly BigInteger Clamp =
        BigInteger.Parse("0ffffffc0ffffffc0ffffffc0fffffff", System.Globalization.NumberStyles.HexNumber);

    private static readonly BigInteger Mask128 = (BigInteger.One << 128) - 1;

    private static void Poly1305Mac(ReadOnlySpan<byte> aad, ReadOnlySpan<byte> ciphertext,
        ReadOnlySpan<byte> otk, Span<byte> tag)
    {
        BigInteger r = LittleEndian(otk[..16]) & Clamp;
        BigInteger s = LittleEndian(otk.Slice(16, 16));

        // mac_data = aad ‖ pad16 ‖ ciphertext ‖ pad16 ‖ le64(aad_len) ‖ le64(ct_len).
        int aadPad = (16 - aad.Length % 16) % 16;
        int ctPad = (16 - ciphertext.Length % 16) % 16;
        int total = aad.Length + aadPad + ciphertext.Length + ctPad + 16;
        byte[] macData = new byte[total];

        int offset = 0;
        aad.CopyTo(macData.AsSpan(offset));
        offset += aad.Length + aadPad;
        ciphertext.CopyTo(macData.AsSpan(offset));
        offset += ciphertext.Length + ctPad;
        BinaryPrimitives.WriteUInt64LittleEndian(macData.AsSpan(offset, 8), (ulong)aad.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(macData.AsSpan(offset + 8, 8), (ulong)ciphertext.Length);

        // Длина mac_data всегда кратна 16, поэтому каждый блок полный → добавляем 2^128.
        BigInteger acc = 0;
        Span<byte> block = stackalloc byte[17];
        for (int i = 0; i < total; i += 16)
        {
            macData.AsSpan(i, 16).CopyTo(block);
            block[16] = 1;
            acc = (acc + LittleEndian(block)) * r % Prime;
        }

        acc = (acc + s) & Mask128;
        tag.Clear();
        acc.TryWriteBytes(tag, out _, isUnsigned: true, isBigEndian: false);
    }

    private static BigInteger LittleEndian(ReadOnlySpan<byte> bytes)
        => new(bytes, isUnsigned: true, isBigEndian: false);
}