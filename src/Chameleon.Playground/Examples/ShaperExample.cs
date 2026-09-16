using System.Diagnostics;
using Chameleon.Core.Session;

namespace Chameleon.Playground.Examples;

/// <summary>Стохастический шейпер: рандомные паузы (не «метроном»), смена режимов и модели на лету.</summary>
public static class ShaperExample
{
    public static async Task RunAsync()
    {
        var shaper = TrafficShaper.WebBrowsing;
        var delays = new List<int>();
        var sizes = new HashSet<int>();
        var regimes = new List<string>();
        var sw = Stopwatch.StartNew();

        Console.WriteLine("модель WebBrowsing: наблюдаем паузы, размеры и смену режимов…");
        while (sw.Elapsed < TimeSpan.FromSeconds(12) && regimes.Distinct().Count() < 2)
        {
            var (delay, size) = shaper.NextCover();
            delays.Add((int)delay.TotalMilliseconds);
            sizes.Add(size);
            string regime = shaper.CurrentRegimeName;
            if (regimes.Count == 0 || regimes[^1] != regime) regimes.Add(regime);
            await Task.Delay(delay);
        }

        Console.WriteLine(
            $"паузы, мс: min={delays.Min()} max={delays.Max()} уникальных={delays.Distinct().Count()} (не метроном)");
        Console.WriteLine($"размеры прикрытия (со ступеней лесенки): [{string.Join(", ", sizes.OrderBy(x => x))}]");
        Console.WriteLine($"режимы за {sw.Elapsed.TotalSeconds:0.0} с: {string.Join(" -> ", regimes)}");

        Console.WriteLine("\nсмена модели на лету -> Streaming:");
        shaper.SwitchModel(TrafficModel.Streaming);
        var s2 = new HashSet<int>();
        for (int i = 0; i < 30; i++)
        {
            s2.Add(shaper.NextCover().Size);
            await Task.Delay(10);
        }

        Console.WriteLine(
            $"модель={shaper.CurrentModelName}, размеры=[{string.Join(", ", s2.OrderBy(x => x))}] (крупнее - это видео)");
    }
}