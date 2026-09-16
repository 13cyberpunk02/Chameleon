using System.Security.Cryptography;
using Chameleon.Core.Fec;

namespace Chameleon.Playground.Examples;

/// <summary>FEC Рида-Соломона: стресс-тест кодека + блочное восстановление пакетов переменной длины.</summary>
public static class FecExample
{
    public static Task RunAsync()
    {
        var rng = new Random(7);
        int fails = 0;
        for (int trial = 0; trial < 1000; trial++)
        {
            int k = rng.Next(2, 12), m = rng.Next(1, 6), size = rng.Next(16, 1024);
            var rs = new ReedSolomon(k, m);
            var data = new byte[k][];
            for (int i = 0; i < k; i++)
            {
                data[i] = new byte[size];
                rng.NextBytes(data[i]);
            }

            var parity = rs.Encode(data);

            var shards = new byte[k + m][];
            for (int i = 0; i < k; i++) shards[i] = (byte[])data[i].Clone();
            for (int i = 0; i < m; i++) shards[k + i] = (byte[])parity[i].Clone();

            var present = Enumerable.Repeat(true, k + m).ToArray();
            foreach (int e in Enumerable.Range(0, k + m).OrderBy(_ => rng.Next()).Take(m))
            {
                present[e] = false;
                shards[e] = new byte[size];
            }

            rs.DecodeMissingData(shards, present);
            for (int i = 0; i < k; i++)
                if (!shards[i].AsSpan().SequenceEqual(data[i]))
                {
                    fails++;
                    break;
                }
        }

        Console.WriteLine(fails == 0
            ? "стресс-тест RS (1000 прогонов): все восстановлены точно ✓"
            : $"провалов: {fails} ✗");

        var fec = new FecBlock(dataShards: 6, parityShards: 2);
        var packets = new byte[6][];
        for (int i = 0; i < 6; i++)
        {
            packets[i] = new byte[rng.Next(20, 400)];
            rng.NextBytes(packets[i]);
        }

        string[] hashes = packets.Select(p => Convert.ToHexString(SHA256.HashData(p))).ToArray();

        byte[][] blockShards = fec.Encode(packets);
        var pres = Enumerable.Repeat(true, fec.TotalShards).ToArray();
        int size2 = blockShards[0].Length;
        foreach (int e in new[] { 1, 5 })
        {
            pres[e] = false;
            blockShards[e] = new byte[size2];
        }

        Console.WriteLine("блочный FEC: 6 пакетов + 2 parity, стёрты шарды 1 и 5…");

        byte[][] recovered = fec.Decode(blockShards, pres);
        bool ok = recovered.Select((p, i) => Convert.ToHexString(SHA256.HashData(p)) == hashes[i]).All(x => x);
        Console.WriteLine(ok ? "все 6 пакетов восстановлены точно (без ретрансмита)" : "восстановление не удалось");
        return Task.CompletedTask;
    }
}