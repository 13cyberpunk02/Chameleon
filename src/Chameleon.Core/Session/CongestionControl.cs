namespace Chameleon.Core.Session;

/// <summary>
/// Контроль перегрузки на уровне сессии: ограничивает число «пакетов в полёте»
/// адаптивным окном (cwnd) и вычисляет адаптивный RTO по измеренному RTT.
///
/// Зачем: без окна отправитель льёт данные без оглядки -> очередь на канале растёт
/// -> фиксированный RTO срабатывает на ещё живые пакеты -> лавина ложных
/// ретрансмитов (пожирает CPU, режет полезную скорость, раздувает память).
///
/// Окно - AIMD, как в TCP Reno: медленный старт (×2 за RTT) до ssthresh, затем
/// линейный рост (+1 за RTT); при потере (RTO) - ssthresh = cwnd/2, откат.
/// RTO - по RFC 6298 (SRTT + 4·RTTVAR), с алгоритмом Карна (RTT только по
/// пакетам без ретрансмита).
/// </summary>
public sealed class CongestionControl(int initialCwnd = 16)
{
    private readonly object _lock = new();
    private readonly SemaphoreSlim _signal = new(0, int.MaxValue);

    private double _cwnd = initialCwnd;
    private double _ssthresh = double.MaxValue;
    private long _inFlight;

    private double _srtt = -1;
    private double _rttVar;

    public int MinCwnd { get; init; } = 4;
    public int MaxCwnd { get; init; } = 4096; // потолок «в полёте» (защита памяти)
    public int MinRtoMs { get; init; } = 100;
    public int MaxRtoMs { get; init; } = 10_000;

    public int RtoMs { get; private set; } = 300;

    public double Cwnd
    {
        get
        {
            lock (_lock) return _cwnd;
        }
    }

    public long InFlight => Interlocked.Read(ref _inFlight);

    /// <summary>Ждёт, пока в окне освободится место под новый пакет, затем занимает слот.</summary>
    public async ValueTask AcquireAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            lock (_lock)
            {
                if (_inFlight < (long)Math.Max(MinCwnd, _cwnd))
                {
                    _inFlight++;
                    return;
                }
            }

            await _signal.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Пакет подтверждён: освобождаем слот, растим окно, обновляем RTT.</summary>
    public void OnAck(int rttSampleMs, bool wasRetransmitted)
    {
        lock (_lock)
        {
            if (_inFlight > 0) _inFlight--;

            if (!wasRetransmitted && rttSampleMs >= 0) UpdateRtt(rttSampleMs); // Карн: не по ретрансмитам

            if (_cwnd < _ssthresh) _cwnd += 1; // медленный старт
            else _cwnd += 1.0 / _cwnd; // избегание перегрузки
            if (_cwnd > MaxCwnd) _cwnd = MaxCwnd;
        }

        _signal.Release();
    }

    /// <summary>Потеря (сработал RTO): мультипликативное уменьшение окна.</summary>
    public void OnLoss()
    {
        lock (_lock)
        {
            _ssthresh = Math.Max(MinCwnd, _cwnd / 2);
            _cwnd = _ssthresh;
        }

        _signal.Release();
    }

    /// <summary>Освободить слот без изменения окна (например, при закрытии).</summary>
    public void ReleaseSlot()
    {
        lock (_lock)
        {
            if (_inFlight > 0) _inFlight--;
        }

        _signal.Release();
    }

    private void UpdateRtt(int r)
    {
        if (_srtt < 0)
        {
            _srtt = r;
            _rttVar = r / 2.0;
        }
        else
        {
            _rttVar = 0.75 * _rttVar + 0.25 * Math.Abs(_srtt - r);
            _srtt = 0.875 * _srtt + 0.125 * r;
        }

        RtoMs = Math.Clamp((int)(_srtt + 4 * _rttVar), MinRtoMs, MaxRtoMs);
    }
}