namespace Chameleon.Core.Session;

/// <summary>
/// Контроль перегрузки на уровне сессии: ограничивает число БАЙТ «в полёте»
/// адаптивным окном (cwnd) и вычисляет адаптивный RTO по измеренному RTT.
///
/// Несущие - TCP/TLS: они сами надёжны и сами регулируют скорость. Поэтому окно
/// здесь - прежде всего предохранитель памяти (неподтверждённые пакеты хранятся
/// для ретрансмита) и тормоз для мультипути, а не основной регулятор. Окно в
/// байтах, а не в пакетах: иначе поток мелких пакетов (сайт отдаёт по 1-2 КБ)
/// упирается в потолок cwnd·размер/RTT задолго до пропускной способности канала.
///
/// Окно - AIMD: медленный старт (+подтверждённые байты) до ssthresh, затем
/// линейный рост; при потере (RTO, только в мультипути) - ssthresh = cwnd/2.
/// RTO - по RFC 6298 (SRTT + 4·RTTVAR), с алгоритмом Карна (RTT только по
/// пакетам без ретрансмита).
/// </summary>
public sealed class CongestionControl
{
    private const int Mss = 16 * 1024;

    private readonly object _lock = new();
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _hasWaiters;
    private Exception? _aborted;

    private double _cwnd;
    private double _ssthresh = double.MaxValue;
    private long _inFlight;

    private double _srtt = -1;
    private double _rttVar;
    private bool _policing;

    public long MinCwnd { get; init; } = 256 * 1024;
    public long MaxCwnd { get; init; } = 16 * 1024 * 1024;
    public int MinRtoMs { get; init; } = 200;
    public int MaxRtoMs { get; init; } = 10_000;

    public CongestionControl(long initialCwndBytes = 1024 * 1024) => _cwnd = initialCwndBytes;

    public int RtoMs { get; private set; } = 1000;

    /// <summary>Сглаженный RTT (мс), 0 пока нет измерений.</summary>
    public int SmoothedRttMs
    {
        get
        {
            lock (_lock) return _srtt < 0 ? 0 : (int)_srtt;
        }
    }

    /// <summary>Окно в байтах.</summary>
    public double Cwnd
    {
        get
        {
            lock (_lock) return _cwnd;
        }
    }

    /// <summary>Байт в полёте.</summary>
    public long InFlight
    {
        get
        {
            lock (_lock) return _inFlight;
        }
    }

    /// <summary>
    /// Режим «полисинг»: потери вызваны искусственным троттлингом (token bucket),
    /// а не переполнением очереди. В этом режиме мультипликативный сброс окна бесполезен
    /// (он лишь режет скорость там, где потолок искусственный), поэтому реакция мягкая.
    /// Выставляется детектором (<see cref="Congestion.PolicingDetector"/>) из сессии.
    /// </summary>
    public bool PolicingMode
    {
        get
        {
            lock (_lock) return _policing;
        }
        set
        {
            lock (_lock) _policing = value;
        }
    }

    /// <summary>Ждёт, пока в окне освободится место под <paramref name="bytes"/>, затем занимает его.
    /// Пустое окно пропускает пакет любого размера (иначе крупный пакет мог бы застрять навсегда).</summary>
    public async ValueTask AcquireAsync(int bytes, CancellationToken cancellationToken)
    {
        while (true)
        {
            Task wait;
            lock (_lock)
            {
                if (_aborted is not null) throw new IOException("Сессия закрыта", _aborted);
                if (_inFlight == 0 || _inFlight + bytes <= (long)Math.Max(MinCwnd, _cwnd))
                {
                    _inFlight += bytes;
                    return;
                }

                _hasWaiters = true;
                wait = _changed.Task;
            }

            await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Пакет подтверждён: освобождаем место, растим окно, обновляем RTT.</summary>
    public void OnAck(int bytes, int rttSampleMs, bool wasRetransmitted)
    {
        TaskCompletionSource? wake;
        lock (_lock)
        {
            _inFlight = Math.Max(0, _inFlight - bytes);

            if (!wasRetransmitted && rttSampleMs >= 0) UpdateRtt(rttSampleMs);

            if (_cwnd < _ssthresh) _cwnd += bytes;
            else _cwnd += (double)Mss * bytes / _cwnd;
            if (_cwnd > MaxCwnd) _cwnd = MaxCwnd;

            wake = TakeWaitersLocked();
        }

        wake?.TrySetResult();
    }

    /// <summary>
    /// Потеря (сработал RTO). При обычной перегрузке - мультипликативное уменьшение окна
    /// (AIMD, cwnd/2). В режиме полисинга - лишь лёгкое поджатие (×0.9): резать скорость
    /// под искусственным потолком бессмысленно, лучше держать окно и/или уйти на другую несущую.
    /// </summary>
    public void OnLoss()
    {
        TaskCompletionSource? wake;
        lock (_lock)
        {
            if (_policing)
            {
                _cwnd = Math.Max(MinCwnd, _cwnd * 0.9);
            }
            else
            {
                _ssthresh = Math.Max(MinCwnd, _cwnd / 2);
                _cwnd = _ssthresh;
            }

            wake = TakeWaitersLocked();
        }

        wake?.TrySetResult();
    }

    /// <summary>Освободить место без изменения окна (например, при закрытии).</summary>
    public void Release(int bytes)
    {
        TaskCompletionSource? wake;
        lock (_lock)
        {
            _inFlight = Math.Max(0, _inFlight - bytes);
            wake = TakeWaitersLocked();
        }

        wake?.TrySetResult();
    }

    /// <summary>
    /// Сессия умерла: все ждущие места в окне (и все будущие) получают исключение,
    /// а не висят вечно - ACK'ов, которые освободили бы окно, уже не будет.
    /// </summary>
    public void Abort(Exception? reason = null)
    {
        TaskCompletionSource old;
        lock (_lock)
        {
            if (_aborted is not null) return;
            _aborted = reason ?? new OperationCanceledException();
            _hasWaiters = false;
            old = _changed;
        }

        old.TrySetResult();
    }

    /// <summary>Будит ждущих только если они есть: без лишних аллокаций и без накопления
    /// «разрешений», как у семафора (из-за которых ждущий потом крутился вхолостую).</summary>
    private TaskCompletionSource? TakeWaitersLocked()
    {
        if (!_hasWaiters) return null;
        _hasWaiters = false;
        var old = _changed;
        _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return old;
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
