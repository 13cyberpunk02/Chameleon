using System.Security.Cryptography;
using System.Text;

namespace Chameleon.Tls;

/// <summary>
/// JA4 - современный отпечаток TLS ClientHello (FoxIO). В отличие от JA3
/// устойчив к перестановке расширений (они сортируются), поэтому именно его
/// используют современные фильтры. Формат: ja4_a _ ja4_b _ ja4_c.
/// </summary>
public static class Ja4
{
    public static string FromClientHelloRecord(ReadOnlySpan<byte> rec)
    {
        int p = 5 + 4;
        int legacyVer = U16(rec, ref p);
        p += 32;
        int sid = rec[p++];
        p += sid;

        int cl = U16(rec, ref p);
        var ciphers = new List<int>();
        for (int i = 0; i < cl; i += 2) ciphers.Add(U16(rec, ref p));
        int comp = rec[p++];
        p += comp;

        var exts = new List<int>();
        var sigAlgs = new List<int>();
        int version = legacyVer;
        bool sni = false;
        string alpnFirst = "";
        if (p < rec.Length)
        {
            int total = U16(rec, ref p);
            int end = p + total;
            while (p < end)
            {
                int t = U16(rec, ref p);
                int el = U16(rec, ref p);
                int s = p;
                exts.Add(t);
                if (t == 0) sni = true;
                else if (t == 43)
                {
                    int list = rec[p++];
                    for (int i = 0; i < list; i += 2)
                    {
                        int v = U16(rec, ref p);
                        if (!Grease(v)) version = Math.Max(version, v);
                    }
                }
                else if (t == 13)
                {
                    int slen = U16(rec, ref p);
                    for (int i = 0; i < slen; i += 2) sigAlgs.Add(U16(rec, ref p));
                }
                else if (t == 16 && alpnFirst.Length == 0)
                {
                    U16(rec, ref p);
                    int n = rec[p++];
                    alpnFirst = Encoding.ASCII.GetString(rec.Slice(p, n));
                }

                p = s + el;
            }
        }

        string verCode = version switch { 0x0304 => "13", 0x0303 => "12", 0x0302 => "11", 0x0301 => "10", _ => "00" };
        var cipNoG = ciphers.Where(c => !Grease(c)).ToList();
        var extNoG = exts.Where(e => !Grease(e)).ToList();
        string alpn = alpnFirst.Length >= 1 ? $"{alpnFirst[0]}{alpnFirst[^1]}" : "00";

        string ja4a =
            $"t{verCode}{(sni ? 'd' : 'i')}{Math.Min(cipNoG.Count, 99):00}{Math.Min(extNoG.Count, 99):00}{alpn}";

        string cipList = string.Join(",", cipNoG.OrderBy(x => x).Select(x => x.ToString("x4")));
        string ja4b = Trunc12(cipList);

        var extForHash = extNoG.Where(e => e != 0 && e != 16).OrderBy(x => x).Select(x => x.ToString("x4"));
        string sig = string.Join(",", sigAlgs.Select(x => x.ToString("x4")));
        string ja4cInput = string.Join(",", extForHash) + "_" + sig;
        string ja4c = Trunc12(ja4cInput);

        return $"{ja4a}_{ja4b}_{ja4c}";
    }

    private static string Trunc12(string s)
    {
        byte[] h = SHA256.HashData(Encoding.ASCII.GetBytes(s));
        return Convert.ToHexString(h).ToLowerInvariant()[..12];
    }

    private static bool Grease(int v) => (v & 0x0f0f) == 0x0a0a && (v >> 8) == (v & 0xff);

    private static int U16(ReadOnlySpan<byte> b, ref int p)
    {
        int v = (b[p] << 8) | b[p + 1];
        p += 2;
        return v;
    }
}