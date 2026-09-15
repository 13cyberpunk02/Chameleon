using System.Numerics;
using System.Security.Cryptography;

namespace Chameleon.Core.Crypto;

public readonly record struct KeyPair(byte[] Private, byte[] Public);

/// <summary>
/// Обмен ключами X25519 (RFC 7748). Реализация на BigInteger: корректная и без
/// внешних зависимостей, но НЕ константного времени. Для исследования и тестов
/// этого достаточно; в боевой сборке стоит заменить на аудированную библиотеку
/// (NSec или BouncyCastle), API совместим.
/// </summary>
public static class X25519
{
    public const int KeySize = 32;

    private static readonly BigInteger P = BigInteger.Pow(2, 255) - 19;
    private static readonly BigInteger A24 = 121665;

    public static KeyPair GenerateKeyPair()
    {
        byte[] priv = RandomNumberGenerator.GetBytes(KeySize);
        return new KeyPair(priv, ScalarMultBase(priv));
    }

    public static byte[] ScalarMultBase(ReadOnlySpan<byte> scalar)
    {
        Span<byte> basePoint = stackalloc byte[KeySize];
        basePoint[0] = 9;
        return ScalarMult(scalar, basePoint);
    }

    public static byte[] ScalarMult(ReadOnlySpan<byte> scalar, ReadOnlySpan<byte> uCoordinate)
    {
        if (scalar.Length != KeySize || uCoordinate.Length != KeySize)
            throw new ArgumentException("Ключ должен быть 32 байта");

        Span<byte> k = stackalloc byte[KeySize];
        scalar.CopyTo(k);
        k[0] &= 248;
        k[31] &= 127;
        k[31] |= 64;

        Span<byte> u = stackalloc byte[KeySize];
        uCoordinate.CopyTo(u);
        u[31] &= 127;
        BigInteger x1 = DecodeLittleEndian(u);

        BigInteger x2 = 1, z2 = 0, x3 = x1, z3 = 1;
        int swap = 0;

        for (int t = 254; t >= 0; t--)
        {
            int bit = (k[t >> 3] >> (t & 7)) & 1;
            swap ^= bit;
            ConditionalSwap(swap, ref x2, ref x3);
            ConditionalSwap(swap, ref z2, ref z3);
            swap = bit;

            BigInteger a = Mod(x2 + z2);
            BigInteger aa = Mod(a * a);
            BigInteger b = Mod(x2 - z2);
            BigInteger bb = Mod(b * b);
            BigInteger e = Mod(aa - bb);
            BigInteger c = Mod(x3 + z3);
            BigInteger d = Mod(x3 - z3);
            BigInteger da = Mod(d * a);
            BigInteger cb = Mod(c * b);

            BigInteger t0 = Mod(da + cb);
            x3 = Mod(t0 * t0);
            BigInteger t1 = Mod(da - cb);
            z3 = Mod(Mod(t1 * t1) * x1);
            x2 = Mod(aa * bb);
            z2 = Mod(e * (aa + Mod(A24 * e)));
        }

        ConditionalSwap(swap, ref x2, ref x3);
        ConditionalSwap(swap, ref z2, ref z3);

        BigInteger result = Mod(x2 * BigInteger.ModPow(z2, P - 2, P));
        return EncodeLittleEndian(result);
    }

    private static void ConditionalSwap(int swap, ref BigInteger a, ref BigInteger b)
    {
        if (swap == 1) (a, b) = (b, a);
    }

    private static BigInteger Mod(BigInteger value)
    {
        BigInteger r = value % P;
        return r.Sign < 0 ? r + P : r;
    }

    private static BigInteger DecodeLittleEndian(ReadOnlySpan<byte> bytes)
    {
        Span<byte> buffer = stackalloc byte[KeySize + 1];
        bytes.CopyTo(buffer);
        buffer[KeySize] = 0; // гарантируем неотрицательность
        return new BigInteger(buffer);
    }

    private static byte[] EncodeLittleEndian(BigInteger value)
    {
        byte[] result = new byte[KeySize];
        value.TryWriteBytes(result, out _, isUnsigned: true, isBigEndian: false);
        return result;
    }
}