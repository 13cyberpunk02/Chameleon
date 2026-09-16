using Chameleon.Core.Session;

namespace Chameleon.Playground.Examples;

/// <summary>Обучение модели поведения из сэмплов трафика, сохранение в JSON, загрузка, работа.</summary>
public static class TrafficModelExample
{
    public static Task RunAsync()
    {
        var rng = new Random(42);
        var sizes = new List<int>();
        var delays = new List<int>();
        for (int i = 0; i < 500; i++)
        {
            bool burst = rng.NextDouble() < 0.7;
            sizes.Add(burst ? rng.Next(60, 600) : rng.Next(1200, 16000));
            delays.Add(burst ? rng.Next(10, 80) : rng.Next(300, 1000));
        }

        Console.WriteLine(
            $"наблюдений: {sizes.Count} (размеры {sizes.Min()}..{sizes.Max()}, паузы {delays.Min()}..{delays.Max()} мс)");

        TrafficModel fitted = TrafficModelIo.FromSamples("fitted-web", sizes, delays);
        Console.WriteLine(
            $"обучена модель «{fitted.Name}»: лесенка=[{string.Join(", ", fitted.SizeLadder)}], режимов={fitted.Regimes.Length}");

        string json = TrafficModelIo.ToJson(fitted);
        TrafficModel reloaded = TrafficModelIo.FromJson(json);
        bool same = reloaded.Name == fitted.Name && reloaded.SizeLadder.SequenceEqual(fitted.SizeLadder);
        Console.WriteLine($"JSON round-trip: {(same ? "✓ идентична после сохранения/загрузки" : "✗ расхождение")}");

        var shaper = new TrafficShaper(reloaded);
        var seen = new HashSet<int>();
        for (int i = 0; i < 40; i++) seen.Add(shaper.NextCover().Size);
        Console.WriteLine(
            $"шейпер на обученной модели: размеры ⊂ лесенка: {(seen.All(reloaded.SizeLadder.Contains) ? "✓" : "✗")}");
        return Task.CompletedTask;
    }
}