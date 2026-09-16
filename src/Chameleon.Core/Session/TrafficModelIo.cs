using System.Text;
using System.Text.Json;

namespace Chameleon.Core.Session;

/// <summary>
/// Загрузка/сохранение <see cref="TrafficModel"/> в JSON и простое построение
/// модели из сэмплов реального трафика. Используется Utf8JsonWriter/JsonDocument
/// (без рефлексии), поэтому совместимо с Native AOT и не тянет зависимостей.
///
/// Схема JSON:
/// {
///   "name": "web-browsing",
///   "sizeLadder": [128, 512, 1536, 4096, 16384],
///   "regimes": [
///     { "name":"burst", "dwellMinMs":400, "dwellMaxMs":1500,
///       "coverDelayMinMs":15, "coverDelayMaxMs":90,
///       "sizeWeights":[{"size":128,"weight":6}, ...],
///       "transitions":[{"to":0,"weight":1},{"to":1,"weight":4}] }
///   ]
/// }
/// </summary>
public static class TrafficModelIo
{
    public static string ToJson(TrafficModel model)
    {
        var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("name", model.Name);

            w.WriteStartArray("sizeLadder");
            foreach (int size in model.SizeLadder) w.WriteNumberValue(size);
            w.WriteEndArray();

            w.WriteStartArray("regimes");
            foreach (var r in model.Regimes)
            {
                w.WriteStartObject();
                w.WriteString("name", r.Name);
                w.WriteNumber("dwellMinMs", r.DwellMinMs);
                w.WriteNumber("dwellMaxMs", r.DwellMaxMs);
                w.WriteNumber("coverDelayMinMs", r.CoverDelayMinMs);
                w.WriteNumber("coverDelayMaxMs", r.CoverDelayMaxMs);

                w.WriteStartArray("sizeWeights");
                foreach (var (size, weight) in r.SizeWeights)
                {
                    w.WriteStartObject();
                    w.WriteNumber("size", size);
                    w.WriteNumber("weight", weight);
                    w.WriteEndObject();
                }

                w.WriteEndArray();

                w.WriteStartArray("transitions");
                foreach (var (to, weight) in r.Transitions)
                {
                    w.WriteStartObject();
                    w.WriteNumber("to", to);
                    w.WriteNumber("weight", weight);
                    w.WriteEndObject();
                }

                w.WriteEndArray();

                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public static TrafficModel FromJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        string name = root.GetProperty("name").GetString() ?? "model";
        var ladder = new List<int>();
        foreach (var s in root.GetProperty("sizeLadder").EnumerateArray()) ladder.Add(s.GetInt32());

        var regimes = new List<TrafficRegime>();
        foreach (var r in root.GetProperty("regimes").EnumerateArray())
        {
            var sizeWeights = new List<(int, int)>();
            foreach (var sw in r.GetProperty("sizeWeights").EnumerateArray())
                sizeWeights.Add((sw.GetProperty("size").GetInt32(), sw.GetProperty("weight").GetInt32()));

            var transitions = new List<(int, int)>();
            foreach (var t in r.GetProperty("transitions").EnumerateArray())
                transitions.Add((t.GetProperty("to").GetInt32(), t.GetProperty("weight").GetInt32()));

            regimes.Add(new TrafficRegime(
                r.GetProperty("name").GetString() ?? "regime",
                r.GetProperty("dwellMinMs").GetInt32(), r.GetProperty("dwellMaxMs").GetInt32(),
                r.GetProperty("coverDelayMinMs").GetInt32(), r.GetProperty("coverDelayMaxMs").GetInt32(),
                [.. sizeWeights], [.. transitions]));
        }

        return new TrafficModel(name, [.. ladder], [.. regimes]);
    }

    /// <summary>
    /// Строит модель из наблюдённого трафика: размеров пакетов и пауз между ними.
    /// Сэмплы делятся по медиане паузы на «быстрый» (всплеск) и «медленный»
    /// (затишье) режимы; размеры квантуются в лесенку по перцентилям, веса берутся
    /// из гистограммы. Простая, но настоящая подгонка под реальные данные.
    /// </summary>
    public static TrafficModel FromSamples(string name, IReadOnlyList<int> packetSizes,
        IReadOnlyList<int> interArrivalMs)
    {
        if (packetSizes.Count == 0 || interArrivalMs.Count == 0)
            throw new ArgumentException("Нужны непустые выборки размеров и пауз");

        int cap = Crypto.RecordFormat.MaxPlaintext;
        int[] ladder = BuildLadder(packetSizes, cap);

        int[] sortedDelays = interArrivalMs.OrderBy(x => x).ToArray();
        int median = sortedDelays[sortedDelays.Length / 2];

        // Разбиваем размеры по режимам через сопоставление с паузами (по индексу, где возможно).
        var fast = new List<int>();
        var slow = new List<int>();
        for (int i = 0; i < packetSizes.Count; i++)
        {
            int delay = interArrivalMs[Math.Min(i, interArrivalMs.Count - 1)];
            (delay <= median ? fast : slow).Add(packetSizes[i]);
        }

        if (fast.Count == 0) fast.AddRange(packetSizes);
        if (slow.Count == 0) slow.AddRange(packetSizes);

        var fastDelays = interArrivalMs.Where(d => d <= median).DefaultIfEmpty(median).ToArray();
        var slowDelays = interArrivalMs.Where(d => d > median).DefaultIfEmpty(median).ToArray();

        var burst = new TrafficRegime("burst",
            DwellMinMs: 400, DwellMaxMs: 1500,
            CoverDelayMinMs: Percentile(fastDelays, 25),
            CoverDelayMaxMs: Math.Max(Percentile(fastDelays, 75), Percentile(fastDelays, 25) + 1),
            SizeWeights: Histogram(fast, ladder),
            Transitions: [(0, 1), (1, 4)]);

        var quiet = new TrafficRegime("quiet",
            DwellMinMs: 1200, DwellMaxMs: 5000,
            CoverDelayMinMs: Percentile(slowDelays, 25),
            CoverDelayMaxMs: Math.Max(Percentile(slowDelays, 75), Percentile(slowDelays, 25) + 1),
            SizeWeights: Histogram(slow, ladder),
            Transitions: [(0, 3), (1, 1)]);

        return new TrafficModel(name, ladder, [burst, quiet]);
    }

    private static int[] BuildLadder(IReadOnlyList<int> sizes, int cap)
    {
        int[] sorted = sizes.OrderBy(x => x).ToArray();
        var steps = new SortedSet<int>();
        foreach (int pct in new[] { 50, 75, 90, 99 })
            steps.Add(Math.Min(cap, Math.Max(1, sorted[(int)((sorted.Length - 1) * pct / 100.0)])));
        steps.Add(Math.Min(cap, sorted[^1]));
        return [.. steps];
    }

    private static (int Size, int Weight)[] Histogram(IReadOnlyList<int> sizes, int[] ladder)
    {
        var counts = new int[ladder.Length];
        foreach (int s in sizes)
        {
            int idx = 0;
            while (idx < ladder.Length - 1 && s > ladder[idx]) idx++;
            counts[idx]++;
        }

        var result = new List<(int, int)>();
        for (int i = 0; i < ladder.Length; i++)
            if (counts[i] > 0)
                result.Add((ladder[i], counts[i]));
        if (result.Count == 0) result.Add((ladder[0], 1));
        return [.. result];
    }

    private static int Percentile(int[] values, int pct)
    {
        if (values.Length == 0) return 0;
        int[] sorted = values.OrderBy(x => x).ToArray();
        return sorted[(int)((sorted.Length - 1) * pct / 100.0)];
    }
}