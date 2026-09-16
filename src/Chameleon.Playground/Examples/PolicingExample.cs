using Chameleon.Core.Congestion;

namespace Chameleon.Playground.Examples;

/// <summary>Детектор полисинга vs перегрузки на двух синтетических трассах.</summary>
public static class PolicingExample
{
    public static Task RunAsync()
    {
        Console.WriteLine("Трасса A - ПЕРЕГРУЗКА (RTT раздувается перед потерями):");
        var congestion = new PolicingDetector();
        long t = 0;
        double rtt = 20;
        for (int i = 0; i < 40; i++)
        {
            t += 100;
            rtt = Math.Min(140, rtt + 3);
            congestion.AddSample(t, rtt, 120_000, rtt > 90 ? 2 : 0);
        }

        Print(congestion.Analyze());

        Console.WriteLine("\nТрасса B - ПОЛИСИНГ (RTT ровный, потери у потолка):");
        var policing = new PolicingDetector();
        t = 0;
        var rnd = new Random(1);
        for (int i = 0; i < 40; i++)
        {
            t += 100;
            policing.AddSample(t, 20 + rnd.NextDouble() * 4, 100_000, i > 8 && rnd.NextDouble() < 0.5 ? 1 : 0);
        }

        Print(policing.Analyze());

        var a = congestion.Analyze();
        var b = policing.Analyze();
        Console.WriteLine(a.Cause == LossCause.Congestion && b.Cause == LossCause.Policing
            ? "\nдетектор верно различил обе трассы"
            : "\nошибка классификации");
        return Task.CompletedTask;

        static void Print(LossVerdict v)
        {
            Console.WriteLine($"  причина={v.Cause}, уверенность={v.Confidence:P0}, базовый RTT={v.BaselineRttMs:0} мс"
                              + (v.EstimatedPolicedRateBytesPerSec > 0
                                  ? $", потолок≈{v.EstimatedPolicedRateBytesPerSec / 125_000:0.0} Мбит/с"
                                  : ""));
        }
    }
}