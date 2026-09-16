using System.Security.Cryptography;
using Chameleon.Core.Fec;

Console.WriteLine("=== стресс-тест Reed-Solomon (1000 прогонов) ===");
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

    // стираем ровно m случайных шардов (максимум, что RS может восстановить)
    var present = Enumerable.Repeat(true, k + m).ToArray();
    var idx = Enumerable.Range(0, k + m).OrderBy(_ => rng.Next()).Take(m).ToArray();
    foreach (int e in idx)
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

Console.WriteLine(fails == 0 ? "  все 1000 прогонов восстановлены точно ✓" : $"  провалов: {fails} ✗");

// 2) Блочный FEC на пакетах переменной длины: теряем m несущих из k+m.
Console.WriteLine("\n=== блочный FEC: пакеты переменной длины, потеря 2 «несущих» ===");
var fec = new FecBlock(dataShards: 6, parityShards: 2); // переживаем потерю любых 2 из 8
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
} // «умерли» шарды 1 и 5

Console.WriteLine("  стёрты шарды 1 и 5, восстанавливаем блок…");

byte[][] recovered = fec.Decode(blockShards, pres);
bool allOk = recovered.Select((p, i) => Convert.ToHexString(SHA256.HashData(p)) == hashes[i]).All(x => x);
Console.WriteLine(allOk ? "  все 6 пакетов восстановлены точно (без ретрансмита) ✓" : "  восстановление не удалось ✗");

Console.WriteLine(fails == 0 && allOk
    ? "\nИТОГ: FEC работает - потери до m шардов восстанавливаются без ретрансмитов."
    : "\nИТОГ: есть проблемы.");