using System.Collections.Generic;
using System.Numerics;

namespace Chameleon.Mobile.Android.Vpn;

/// <summary>
/// Построение маршрутов, покрывающих ВЕСЬ IPv4, КРОМЕ заданных подсетей.
/// Нужно для Android &lt; 13, где нет VpnService.Builder.excludeRoute: туда
/// нельзя «вычесть» адрес из 0.0.0.0/0, приходится добавлять дополнение.
/// </summary>
internal static class RouteComplement
{
    /// <param name="excludes">(сеть как uint, префикс) IPv4-подсети для исключения.</param>
    /// <returns>Список маршрутов (ip, prefix), которые нужно AddRoute.</returns>
    public static List<(string Ip, int Prefix)> Build(IEnumerable<(uint Net, int Prefix)> excludes)
    {
        var ranges = new List<(ulong lo, ulong hi)>();
        foreach (var (net, pfx) in excludes)
        {
            ulong size = 1UL << (32 - pfx);
            ulong start = net & (uint)(0xFFFFFFFFUL ^ (size - 1));
            ranges.Add((start, start + size - 1));
        }

        ranges.Sort((a, b) => a.lo.CompareTo(b.lo));

        var merged = new List<(ulong lo, ulong hi)>();
        foreach (var r in ranges)
        {
            if (merged.Count > 0 && r.lo <= merged[^1].hi + 1)
                merged[^1] = (merged[^1].lo, r.hi > merged[^1].hi ? r.hi : merged[^1].hi);
            else
                merged.Add(r);
        }

        var cidrs = new List<(string, int)>();
        ulong cur = 0, max = 0xFFFFFFFFUL;
        foreach (var (lo, hi) in merged)
        {
            if (lo > cur) AddRange(cidrs, cur, lo - 1);
            cur = hi + 1;
            if (cur > max) return cidrs;
        }

        if (cur <= max) AddRange(cidrs, cur, max);
        return cidrs;
    }

    private static void AddRange(List<(string, int)> outList, ulong lo, ulong hi)
    {
        while (lo <= hi)
        {
            int maxAlign;
            if (lo == 0) maxAlign = 32;
            else
            {
                maxAlign = 0;
                var v = lo;
                while ((v & 1) == 0 && maxAlign < 32)
                {
                    v >>= 1;
                    maxAlign++;
                }
            }

            var remaining = hi - lo + 1;
            var maxByCount = 63 - BitOperations.LeadingZeroCount(remaining);
            var size = maxAlign < maxByCount ? maxAlign : maxByCount;

            outList.Add((UIntToIp((uint)lo), 32 - size));
            lo += 1UL << size;
            if (lo == 0) break;
        }
    }

    private static string UIntToIp(uint v) => $"{(v >> 24) & 0xFF}.{(v >> 16) & 0xFF}.{(v >> 8) & 0xFF}.{v & 0xFF}";
}