using Chameleon.Core.Crypto;

namespace Chameleon.Core.Session;

/// <summary>
/// Один режим поведения внутри модели: как долго в нём находиться, с какими
/// паузами и размерами слать прикрытие, и куда переходить дальше.
/// </summary>
public sealed record TrafficRegime(
    string Name,
    int DwellMinMs,
    int DwellMaxMs,
    int CoverDelayMinMs,
    int CoverDelayMaxMs,
    (int Size, int Weight)[] SizeWeights,
    (int To, int Weight)[] Transitions);

/// <summary>
/// Модель поведения трафика: лесенка размеров (для скрытия настоящих длин) и
/// набор режимов конечного автомата (для правдоподобных таймингов прикрытия).
/// Неизменяема; подставляется в <see cref="TrafficShaper"/> и может меняться на лету.
/// </summary>
public sealed class TrafficModel
{
    public TrafficModel(string name, int[] sizeLadder, TrafficRegime[] regimes)
    {
        Name = name;
        SizeLadder = (int[])sizeLadder.Clone();
        Array.Sort(SizeLadder);
        Regimes = regimes;
    }

    public string Name { get; }
    public int[] SizeLadder { get; }
    public TrafficRegime[] Regimes { get; }
    public bool HasCover => Regimes.Length > 0;

    /// <summary>Шейпинг выключен.</summary>
    public static TrafficModel Off { get; } = new("off", [], []);

    /// <summary>
    /// «Просмотр сайтов»: чередование активных всплесков (частые мелкие пакеты) и
    /// затиший (редкие мелкие пакеты). Тайминги рандомизированы - постоянного
    /// «метронома» нет.
    /// </summary>
    public static TrafficModel WebBrowsing { get; } = new("web-browsing",
        sizeLadder: [128, 512, 1536, 4096, RecordFormat.MaxPlaintext],
        regimes:
        [
            new TrafficRegime("burst",
                DwellMinMs: 400, DwellMaxMs: 1500,
                CoverDelayMinMs: 15, CoverDelayMaxMs: 90,
                SizeWeights: [(128, 6), (512, 3), (1536, 2), (4096, 1)],
                Transitions: [(0, 1), (1, 4)]),
            new TrafficRegime("quiet",
                DwellMinMs: 1200, DwellMaxMs: 5000,
                CoverDelayMinMs: 250, CoverDelayMaxMs: 1200,
                SizeWeights: [(128, 8), (512, 2)],
                Transitions: [(0, 3), (1, 1)]),
        ]);

    /// <summary>«Видео»: крупные пакеты, ровнее и плотнее, чем просмотр сайтов.</summary>
    public static TrafficModel Streaming { get; } = new("streaming",
        sizeLadder: [512, 1536, 4096, RecordFormat.MaxPlaintext],
        regimes:
        [
            new TrafficRegime("play",
                DwellMinMs: 3000, DwellMaxMs: 8000,
                CoverDelayMinMs: 30, CoverDelayMaxMs: 120,
                SizeWeights: [(4096, 3), (RecordFormat.MaxPlaintext, 5), (1536, 2)],
                Transitions: [(0, 5), (1, 1)]),
            new TrafficRegime("buffer-wait",
                DwellMinMs: 500, DwellMaxMs: 2000,
                CoverDelayMinMs: 200, CoverDelayMaxMs: 700,
                SizeWeights: [(512, 5), (1536, 3)],
                Transitions: [(0, 4), (1, 1)]),
        ]);
}

/// <summary>
/// Скрывает два поведенческих признака туннеля:
///  1) размеры - реальные record'ы дополняются PADDING'ом до ближайшей ступени
///    лесенки, поэтому настоящих длин на проводе не видно (<see cref="Quantize"/>);
///  2) тайминги - прикрытие в простое шлётся не с постоянным периodом, а по
///    конечному автомату модели: паузы и размеры берутся из распределений
///    режима, режимы меняются вероятностно (<see cref="NextCover"/>).
///
/// Модель можно сменить на лету через <see cref="SwitchModel"/> - приёмная
/// сторона ничего не знает про модель, поэтому смена не ломает соединение.
/// </summary>
public sealed class TrafficShaper
{
    private readonly object _gate = new();
    private TrafficModel _model;
    private int _regimeIndex;
    private long _regimeExpiresAtTicks;

    public TrafficShaper(TrafficModel model)
    {
        _model = model;
        _regimeExpiresAtTicks = long.MinValue;
    }

    public static TrafficShaper Off { get; } = new(TrafficModel.Off);
    public static TrafficShaper WebBrowsing => new(TrafficModel.WebBrowsing);
    public static TrafficShaper Streaming => new(TrafficModel.Streaming);

    public bool Enabled => _model.SizeLadder.Length > 0;
    public bool CoverActive => _model.HasCover;
    public int LargestSize => _model.SizeLadder.Length > 0 ? _model.SizeLadder[^1] : RecordFormat.MaxPlaintext;

    public string CurrentModelName => _model.Name;

    public string CurrentRegimeName
    {
        get
        {
            lock (_gate) return _model.Regimes.Length > 0 ? _model.Regimes[_regimeIndex].Name : "-";
        }
    }

    /// <summary>Ближайшая ступень не меньше длины (или максимум лесенки).</summary>
    public int Quantize(int plaintextLength)
    {
        foreach (int size in _model.SizeLadder)
            if (plaintextLength <= size)
                return size;
        return LargestSize;
    }

    /// <summary>
    /// Следующий пакет прикрытия: пауза перед ним и его размер. Внутри двигает
    /// конечный автомат (смена режима по истечении времени пребывания).
    /// </summary>
    public (TimeSpan Delay, int Size) NextCover()
    {
        lock (_gate)
        {
            long now = Environment.TickCount64;
            if (now >= _regimeExpiresAtTicks)
            {
                _regimeIndex = PickWeighted(_model.Regimes[_regimeIndex].Transitions, fallback: _regimeIndex);
                TrafficRegime entered = _model.Regimes[_regimeIndex];
                _regimeExpiresAtTicks = now + Random.Shared.Next(entered.DwellMinMs, entered.DwellMaxMs + 1);
            }

            TrafficRegime r = _model.Regimes[_regimeIndex];
            int delayMs = Random.Shared.Next(r.CoverDelayMinMs, r.CoverDelayMaxMs + 1);
            int size = PickWeightedSize(r.SizeWeights);
            return (TimeSpan.FromMilliseconds(delayMs), size);
        }
    }

    /// <summary>Сменить модель поведения на лету.</summary>
    public void SwitchModel(TrafficModel model)
    {
        lock (_gate)
        {
            _model = model;
            _regimeIndex = 0;
            _regimeExpiresAtTicks = long.MinValue;
        }
    }

    private static int PickWeighted((int To, int Weight)[] options, int fallback)
    {
        int total = 0;
        foreach (var (_, weight) in options) total += weight;
        if (total <= 0) return fallback;

        int roll = Random.Shared.Next(total);
        foreach (var (to, weight) in options)
        {
            roll -= weight;
            if (roll < 0) return to;
        }

        return fallback;
    }

    private static int PickWeightedSize((int Size, int Weight)[] options)
    {
        int total = 0;
        foreach (var (_, weight) in options) total += weight;
        int roll = Random.Shared.Next(total);
        foreach (var (size, weight) in options)
        {
            roll -= weight;
            if (roll < 0) return size;
        }

        return options[^1].Size;
    }
}