using Chameleon.Tls;

namespace Chameleon.Playground.Examples;

/// <summary>Считает JA3/JA4 нашего ClientHello и сверяет с эталоном Chromium/Brave.</summary>
public static class Ja4Example
{
    private const string TargetJa4 = "t13d1516h2_8daaf6152771_806a8c22fdea";

    public static async Task RunAsync()
    {
        var r = await FingerprintProbe.MeasureAsync();
        Console.WriteLine($"ClientHello: {r.ClientHelloBytes} байт");
        Console.WriteLine($"JA3: {r.Ja3}");
        Console.WriteLine($"JA4:  {r.Ja4}");
        Console.WriteLine($"ЦЕЛЬ: {TargetJa4} (Brave/Chromium)");
        Console.WriteLine(r.Ja4 == TargetJa4
            ? "JA4 совпадает с эталоном браузера бит-в-бит ✓"
            : "JA4 отличается (возможно, обновилась версия браузера - обнови профиль)");
        Console.WriteLine("\nПримечание: JA3 под Chromium на стоковом BouncyCastle недостижим");
        Console.WriteLine("(порядок расширений + GREASE); фильтруют по JA4 - он совпадает.");
    }
}