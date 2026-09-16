namespace Chameleon.Core.Congestion;

/// <summary>Причина потерь, определённая по поведению задержки.</summary>
public enum LossCause
{
    Unknown,
    Congestion,
    Policing,
}

/// <summary>Вердикт анализа: причина, уверенность, оценка «разрешённой» скорости и базовый RTT.</summary>
public readonly record struct LossVerdict(
    LossCause Cause,
    double Confidence,
    double EstimatedPolicedRateBytesPerSec,
    double BaselineRttMs);

/// <summary>
/// Различает перегрузку и полисинг по связи потерь с задержкой (упрощённо, по
/// мотивам Flach et al., «An Internet-Wide Analysis of Traffic Policing», 2016).
///
/// Идея: настоящая перегрузка забивает очередь - RTT растёт ПЕРЕД потерями.
/// Полисер (token bucket) отбрасывает пакеты, не создавая очереди - потери
/// происходят при RTT около базового, а пропускная способность упирается в
/// стабильный потолок. Различив их, congestion control может не «сбрасывать
/// скорость» там, где это лишь искусственный лимит, а сменить стратегию или
/// несущую.
///
/// Класс без зависимостей и без состояния сети - только математика над сэмплами,
/// поэтому легко тестируется на синтетических трассах.
/// </summary>
public sealed class PolicingDetector(int maxSamples = 512)
{
    private readonly record struct Sample(long TimestampMs, double RttMs, long BytesDelivered, int LossCount);

    private readonly List<Sample> _samples = [];

    /// <summary>Во сколько раз RTT должен превысить базовый, чтобы считать потерю «от перегрузки».</summary>
    public double CongestionRttRatio { get; init; } = 1.3;

    public void AddSample(long timestampMs, double rttMs, long bytesDelivered, int lossCount)
    {
        _samples.Add(new Sample(timestampMs, rttMs, bytesDelivered, lossCount));
        if (_samples.Count > maxSamples)
            _samples.RemoveAt(0);
    }

    public LossVerdict Analyze()
    {
        if (_samples.Count < 4)
            return new LossVerdict(LossCause.Unknown, 0, 0, 0);

        double baselineRtt = _samples.Min(s => s.RttMs);
        if (baselineRtt <= 0) baselineRtt = 1;

        var lossSamples = _samples.Where(s => s.LossCount > 0).ToList();
        if (lossSamples.Count == 0)
            return new LossVerdict(LossCause.Unknown, 0, 0, baselineRtt);

        int policingLike = 0, congestionLike = 0;
        foreach (var s in lossSamples)
        {
            if (s.RttMs <= baselineRtt * CongestionRttRatio) policingLike++;
            else congestionLike++;
        }

        int total = policingLike + congestionLike;
        double policingFraction = (double)policingLike / total;

        LossCause cause = policingFraction >= 0.5 ? LossCause.Policing : LossCause.Congestion;
        double confidence = Math.Abs(policingFraction - 0.5) * 2.0;

        double rate = EstimateRate(lossSamples);

        return new LossVerdict(cause, confidence, rate, baselineRtt);
    }

    private double EstimateRate(List<Sample> lossSamples)
    {
        double sumRate = 0;
        int n = 0;
        for (int i = 1; i < _samples.Count; i++)
        {
            if (_samples[i].LossCount == 0) continue;
            double intervalSec = (_samples[i].TimestampMs - _samples[i - 1].TimestampMs) / 1000.0;
            if (intervalSec <= 0) continue;
            sumRate += _samples[i].BytesDelivered / intervalSec;
            n++;
        }

        return n > 0 ? sumRate / n : 0;
    }
}