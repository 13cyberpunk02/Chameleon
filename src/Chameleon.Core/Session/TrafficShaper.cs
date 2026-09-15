using Chameleon.Core.Crypto;

namespace Chameleon.Core.Session;

/// <summary>
/// Модель поведения трафика. Решает две вещи, по которым DPU отличает туннель от
/// обычного HTTPS: размеры пакетов и наличие пауз.
///
///  • Квантование размеров - каждый record дополняется PADDING'ом до одного из
///    немногих «ступенчатых» размеров, поэтому на проводе не видно настоящих
///    длин полезной нагрузки; их всего несколько фиксированных значений.
///  • Прикрытие в простое - пока данных нет, с заданным периодом отправляются
///    пустые (только PADDING) пакеты, чтобы не было характерного паттерна
///    «тишина, затем всплеск», на котором палится ровный проксирующий поток.
///
/// Это первая модель. Дальше сюда встанет полноценный конечный автомат, который
/// можно обучать на реальных записях трафика (просмотр сайтов, видео, звонок) и
/// менять на лету без обновления клиента.
/// </summary>
public sealed class TrafficShaper
{
    private readonly int[] _sizeLadder;

    private TrafficShaper(int[] sizeLadder, TimeSpan idleCoverInterval)
    {
        _sizeLadder = sizeLadder;
        Array.Sort(_sizeLadder);
        IdleCoverInterval = idleCoverInterval;
    }

    /// <summary>Шейпинг выключен: размеры и тайминги как есть.</summary>
    public static TrafficShaper Off { get; } = new([], TimeSpan.Zero);

    /// <summary>Пример модели «просмотр сайтов»: несколько размеров и частое прикрытие.</summary>
    public static TrafficShaper WebBrowsing { get; } = new(
        sizeLadder: [128, 512, 1536, 4096, RecordFormat.MaxPlaintext],
        idleCoverInterval: TimeSpan.FromMilliseconds(250));

    public bool Enabled => _sizeLadder.Length > 0;
    public TimeSpan IdleCoverInterval { get; }
    public int LargestSize => _sizeLadder.Length > 0 ? _sizeLadder[^1] : RecordFormat.MaxPlaintext;

    /// <summary>Ближайший размер-ступень не меньше длины (или максимум лесенки).</summary>
    public int Quantize(int plaintextLength)
    {
        foreach (int size in _sizeLadder)
            if (plaintextLength <= size)
                return size;
        return LargestSize;
    }

    /// <summary>Случайный размер-ступень для пакета-прикрытия.</summary>
    public int RandomCoverSize() => _sizeLadder[Random.Shared.Next(_sizeLadder.Length)];
}