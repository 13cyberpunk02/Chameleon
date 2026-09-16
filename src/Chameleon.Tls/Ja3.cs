using System.Security.Cryptography;
using System.Text;

namespace Chameleon.Tls;

/// <summary>
/// Вычисляет JA3 из сырых байт ClientHello (TLS record -> handshake).
/// JA3 = md5("Version,Ciphers,Extensions,Groups,PointFormats"), значения GREASE
/// исключаются. Нужен для проверки и подгонки отпечатка под браузер.
/// </summary>
public static class Ja3
{
    public static (string Ja3String, string Md5) FromClientHelloRecord(ReadOnlySpan<byte> record)
    {
        int p = 5;
        p += 4;
        ushort legacyVersion = ReadU16(record, ref p);
        p += 32;
        int sessionIdLen = record[p++]; p += sessionIdLen;

        int cipherBytes = ReadU16(record, ref p);
        var ciphers = new List<int>();
        for (int i = 0; i < cipherBytes; i += 2) ciphers.Add(ReadU16(record, ref p));

        int compLen = record[p++]; p += compLen;

        var extensions = new List<int>();
        var groups = new List<int>();
        var pointFormats = new List<int>();
        if (p < record.Length)
        {
            int extTotal = ReadU16(record, ref p);
            int end = p + extTotal;
            while (p < end)
            {
                int extType = ReadU16(record, ref p);
                int extLen = ReadU16(record, ref p);
                int extStart = p;
                extensions.Add(extType);

                if (extType == 10)
                {
                    int gl = ReadU16(record, ref p);
                    for (int i = 0; i < gl; i += 2) groups.Add(ReadU16(record, ref p));
                }
                else if (extType == 11)
                {
                    int pl = record[p++];
                    for (int i = 0; i < pl; i++) pointFormats.Add(record[p++]);
                }
                p = extStart + extLen;
            }
        }

        string Join(IEnumerable<int> xs) => string.Join("-", xs.Where(v => !IsGrease(v)));
        string ja3 = $"{legacyVersion},{Join(ciphers)},{Join(extensions)},{Join(groups)},{Join(pointFormats)}";

        byte[] hash = MD5.HashData(Encoding.ASCII.GetBytes(ja3));
        return (ja3, Convert.ToHexString(hash).ToLowerInvariant());
    }

    private static bool IsGrease(int v) => (v & 0x0f0f) == 0x0a0a && (v >> 8) == (v & 0xff);

    private static ushort ReadU16(ReadOnlySpan<byte> b, ref int p)
    {
        ushort v = (ushort)((b[p] << 8) | b[p + 1]);
        p += 2;
        return v;
    }
}