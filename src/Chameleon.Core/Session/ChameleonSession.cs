using System.Buffers;
using System.Collections.Concurrent;
using Chameleon.Core.Congestion;
using Chameleon.Core.Fec;
using Chameleon.Core.Protocol;
using Chameleon.Core.Transport;

namespace Chameleon.Core.Session;

/// <summary>Параметры FEC для сессии (Reed-Solomon: k данных + m parity на блок).</summary>
public sealed record FecOptions(int DataShards = 8, int ParityShards = 2, bool Enabled = true)
{
    public static readonly FecOptions On = new();
    public static readonly FecOptions Off = new(8, 2, Enabled: false);

    /// <summary>
    /// По умолчанию FEC выключен. Несущие - TCP: внутри живой несущей потерь нет, а при
    /// её смерти пакеты всё равно пересылаются ретрансмитом. Repair же в мультипути -
    /// это +m/k трафика (25% при 8+2). Включать (<see cref="On"/>) имеет смысл для
    /// несущих, которые реально теряют пакеты.
    /// </summary>
    public static readonly FecOptions Default = Off;
}

/// <summary>
/// Логическая сессия поверх ОДНОЙ ИЛИ НЕСКОЛЬКИХ несущих. Мультиплексирует потоки,
/// раскладывает пакеты по живым несущим, обеспечивает надёжную доставку
/// (ACK + ретрансмиты) и переживает смерть отдельной несущей.
///
/// Каждая несущая надёжна и упорядочена сама по себе (внутри неё record'ы не
/// теряются - иначе рассинхронизируется счётчик шифрования). Потери и
/// переупорядочивание возможны только МЕЖДУ несущими, и именно их закрывают
/// номера пакетов (дедуп + ACK) и offset в STREAM (пересборка).
/// </summary>
public sealed class ChameleonSession : IAsyncDisposable
{
    private const int MaxTrackedReceived = 4096;

    private sealed class Carrier(ICarrierChannel channel)
    {
        public ICarrierChannel Channel { get; } = channel;
        public volatile bool Alive = true;
        public Task? Loop;

        /// <summary>Записей, ждущих отправки в эту несущую (очередь на её write-замке).</summary>
        public int Pending;
    }

    /// <summary>Неподтверждённый пакет. Изменяемый: ретрансмит и запись в несущую
    /// обновляют поля на месте (ACK мог успеть удалить его из словаря - это безвредно).</summary>
    private sealed class InFlight(byte[] plaintext, int length)
    {
        public readonly byte[] Plaintext = plaintext;
        public readonly int Length = length;
        public long SentTicks = Environment.TickCount64;
        public bool Retransmitted;

        /// <summary>Первая запись ещё идёт (несущая может держать её под backpressure сколь угодно долго).</summary>
        public volatile bool Sending = true;

        /// <summary>Несущая, в которую пакет ушёл последним; null - записать не удалось (живых не было).</summary>
        public volatile Carrier? Via;
    }

    private readonly List<Carrier> _carriers = [];
    private readonly bool _isClient;
    private readonly TrafficShaper _shaper;
    private readonly int _maxStreamChunk;

    private readonly ConcurrentDictionary<ulong, ChameleonStream> _streams = new();
    private readonly ConcurrentDictionary<ulong, InFlight> _unacked = new();

    private const int MaxEarlyStreams = 256;
    private const int MaxEarlyFramesPerStream = 512;
    private const int EarlyFrameTtlMs = 30_000;
    private readonly Dictionary<ulong, (long SinceTicks, List<Frame> Frames)> _early = new();

    /// <summary>Недавно закрытые потоки собеседника (id -> когда). Под замком <see cref="_early"/>.</summary>
    private readonly Dictionary<ulong, long> _closedPeerStreams = new();
    private const int ClosedStreamTtlMs = 120_000;
    private readonly SortedSet<ulong> _received = [];
    private readonly object _receivedLock = new();
    private readonly CancellationTokenSource _cts = new();

    private long _nextPacketNumber;
    private long _nextStreamId;
    private long _lastSendTicks;
    private long _bytesSent;
    private long _bytesReceived;
    private readonly CongestionControl _cc = new();
    private int _ackPending;
    private Carrier? _ackVia;
    private Task? _ackLoop;
    
    private const int FecReserve = 160;
    private readonly FecEncoder? _fec;
    private readonly FecDecoder? _fecDec;
    private readonly PolicingDetector _detector = new();
    private Task? _policingLoop;
    private long _deliveredBytesInterval;
    private long _lossCountInterval;
    private int _fecRecovered;
    private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Завершается, когда все несущие сессии закрылись (клиент отключился).</summary>
    public Task Completion => _completed.Task;

    private int _rrIndex;
    private Task? _coverLoop;
    private Task? _rtoLoop;

    private ChameleonSession(ICarrierChannel first, bool isClient, TrafficShaper shaper, FecOptions fec)
    {
        _isClient = isClient;
        _shaper = shaper;
        _nextStreamId = isClient ? 1 : 2;
        _lastSendTicks = Environment.TickCount64;
        int cap = Crypto.RecordFormat.MaxPlaintext - 64;
        if (fec.Enabled)
        {
            _fec = new FecEncoder(fec.DataShards, fec.ParityShards);
            _fecDec = new FecDecoder();
            cap = Math.Min(cap, Crypto.RecordFormat.MaxPlaintext - FecReserve);
        }

        _maxStreamChunk = shaper.Enabled ? Math.Min(cap, shaper.LargestSize - 64) : cap;
        _carriers.Add(new Carrier(first));
    }

    public Action<ChameleonStream>? StreamAccepted { get; set; }
    public int CarrierCount
    {
        get
        {
            lock (_carriers) return _carriers.Count(c => c.Alive);
        }
    }
    public int StreamCount => _streams.Count;
    public long BytesSent => Interlocked.Read(ref _bytesSent);
    public long BytesReceived => Interlocked.Read(ref _bytesReceived);
    public int RttMs => _cc.SmoothedRttMs;

    /// <summary>Сколько НОВЫХ пакетов доставлено восстановлением FEC (а не несущей/ретрансмитом). Диагностика.</summary>
    public int FecRecovered => Volatile.Read(ref _fecRecovered);

    /// <summary>Режим полисинга активен (детектор счёл потери искусственными). Диагностика.</summary>
    public bool PolicingDetected => _cc.PolicingMode;

    public static ChameleonSession Start(ICarrierChannel channel, bool isClient,
        TrafficShaper? shaper = null, FecOptions? fec = null)
    {
        var s = new ChameleonSession(channel, isClient, shaper ?? TrafficShaper.Off, fec ?? FecOptions.Default);
        s.StartCarrierLoop(s._carriers[0]);
        if (s._shaper.CoverActive)
            s._coverLoop = Task.Run(() => s.CoverLoopAsync(s._cts.Token));
        s._rtoLoop = Task.Run(() => s.RetransmitLoopAsync(s._cts.Token));
        s._ackLoop = Task.Run(() => s.AckLoopAsync(s._cts.Token));
        s._policingLoop = Task.Run(() => s.PolicingLoopAsync(s._cts.Token));
        return s;
    }

    /// <summary>Добавить ещё одну несущую в живую сессию (ключи выведены для её carrier_id).</summary>
    public void AddCarrier(ICarrierChannel channel)
    {
        var c = new Carrier(channel);
        lock (_carriers) _carriers.Add(c);
        StartCarrierLoop(c);
    }

    private void StartCarrierLoop(Carrier c) => c.Loop = Task.Run(() => ReceiveLoopAsync(c, _cts.Token));
    
    public async ValueTask<ChameleonStream> OpenStreamAsync(
        string host, int port, StreamKind kind = StreamKind.Tcp, CancellationToken cancellationToken = default)
    {
        ulong id = (ulong)Interlocked.Add(ref _nextStreamId, 2) - 2;
        var stream = new ChameleonStream(this, id, kind, host, port);
        _streams[id] = stream;
        await SendReliableAsync(b => BuildStreamOpen(b, NextPacketNumber(), id, kind, host, (ushort)port),
            cancellationToken).ConfigureAwait(false);
        return stream;
    }

    internal async ValueTask SendStreamDataAsync(ulong streamId, ulong offset, ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken)
    {
        int sent = 0;
        while (sent < data.Length)
        {
            int chunk = Math.Min(_maxStreamChunk, data.Length - sent);
            ReadOnlyMemory<byte> slice = data.Slice(sent, chunk);
            await SendReliableAsync(
                b => BuildStreamData(b, NextPacketNumber(), streamId, offset + (ulong)sent, slice.Span),
                cancellationToken).ConfigureAwait(false);
            sent += chunk;
        }
    }

    internal ValueTask SendStreamFinAsync(ulong streamId, ulong finalOffset, CancellationToken cancellationToken)
        => SendReliableAsync(b => BuildStreamFin(b, NextPacketNumber(), streamId, finalOffset), cancellationToken);

    public ValueTask ResetStreamAsync(ulong streamId, ulong errorCode = 0,
        CancellationToken cancellationToken = default)
    {
        ReleaseStream(streamId);
        return SendReliableAsync(b => BuildStreamReset(b, NextPacketNumber(), streamId, errorCode), cancellationToken);
    }

    /// <summary>
    /// Поток больше не нужен локальной стороне (relay закончил с ним работу): убрать из
    /// сессии. Раньше потоки удалялись только при RESET, и на долгой сессии словарь рос
    /// без конца. Поздние фреймы (ретрансмиты, дубли OPEN из мультипути) для такого
    /// потока игнорируются - иначе дубль OPEN заново поднял бы поток и сервер повторно
    /// подключился бы к цели.
    /// </summary>
    public void ReleaseStream(ulong streamId)
    {
        if (_streams.TryRemove(streamId, out var st)) st.CompleteInbound();
        if (!IsPeerStream(streamId)) return;
        lock (_early)
        {
            _early.Remove(streamId);
            _closedPeerStreams[streamId] = Environment.TickCount64;
        }
    }

    private bool IsPeerStream(ulong streamId) => (streamId & 1) == (_isClient ? 0UL : 1UL);

    /// <summary>Отправляет пакет и запоминает его для ретрансмита, пока не придёт ACK.</summary>
    private async ValueTask SendReliableAsync(Func<byte[], int> build, CancellationToken cancellationToken)
    {
        byte[] scratch = ArrayPool<byte>.Shared.Rent(Crypto.RecordFormat.MaxPlaintext);
        try
        {
            int rawLen = build(scratch);
            int length = Pad(scratch, rawLen);
            
            VarInt.TryRead(scratch, out ulong pn, out _);
            byte[] plaintext = scratch.AsSpan(0, length).ToArray();

            await _cc.AcquireAsync(length, cancellationToken).ConfigureAwait(false);
            var inFlight = new InFlight(plaintext, length);
            _unacked[pn] = inFlight;

            try
            {
                inFlight.Via = await SendOnAnyAsync(plaintext, length, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                // Отсчёт RTT/RTO - с момента, когда запись принята несущей, а не с постановки
                // в очередь: иначе ожидание под backpressure раздувало RTT и RTO.
                inFlight.SentTicks = Environment.TickCount64;
                inFlight.Sending = false;
            }

            if (inFlight.Via is null && CarrierCount == 0)
                throw new IOException("Все несущие сессии закрыты");

            // FEC только при мультипути: на одной надёжной несущей repair всё равно не шлётся,
            // а Reed-Solomon на каждый блок - заметный CPU прямо в пути отправки.
            if (_fec is not null && CarrierCount > 1)
            {
                var repairs = _fec.Add(pn, plaintext.AsSpan(0, rawLen), rawLen);
                if (repairs is not null) await SendRepairsAsync(repairs, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }

    /// <summary>
    /// Пишет пакет в живую несущую с наименьшей очередью (при равенстве - по кругу).
    /// Чистый round-robin заставлял ждать самую медленную несущую: каждая запись
    /// await'ится, и суммарная скорость падала до N × скорость худшей. Возвращает
    /// несущую, принявшую пакет, или null, если живых не осталось.
    /// </summary>
    private async ValueTask<Carrier?> SendOnAnyAsync(byte[] plaintext, int length,
        CancellationToken cancellationToken, Carrier? avoid = null)
    {
        while (true)
        {
            Carrier? c = PickCarrier(avoid);
            if (c is null) return null;

            Interlocked.Increment(ref c.Pending);
            try
            {
                await c.Channel.WriteRecordAsync(plaintext.AsMemory(0, length), cancellationToken)
                    .ConfigureAwait(false);
                Interlocked.Add(ref _bytesSent, length);
                Interlocked.Exchange(ref _lastSendTicks, Environment.TickCount64);
                return c;
            }
            catch (Exception)
            {
                c.Alive = false;
            }
            finally
            {
                Interlocked.Decrement(ref c.Pending);
            }
        }
    }

    private Carrier? PickCarrier(Carrier? avoid)
    {
        Carrier[] snapshot;
        lock (_carriers) snapshot = [.. _carriers];
        int n = snapshot.Length;
        int start = (Interlocked.Increment(ref _rrIndex) & int.MaxValue) % n;

        Carrier? best = null, fallback = null;
        for (int i = 0; i < n; i++)
        {
            Carrier c = snapshot[(start + i) % n];
            if (!c.Alive) continue;
            if (ReferenceEquals(c, avoid))
            {
                fallback = c;
                continue;
            }

            if (best is null || Volatile.Read(ref c.Pending) < Volatile.Read(ref best.Pending)) best = c;
        }

        return best ?? fallback;
    }

    /// <summary>Шлёт repair-шарды блока. Только при мультипути: на одной надёжной несущей
    /// потерь нет, parity был бы чистым оверхедом. Repair-пакеты ненадёжны (не ретрансмитятся).</summary>
    private async ValueTask SendRepairsAsync(FecEncoder.Repair[] repairs, CancellationToken cancellationToken)
    {
        if (CarrierCount < 2) return;
        foreach (var r in repairs)
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(Crypto.RecordFormat.MaxPlaintext);
            try
            {
                int length = BuildRepair(buffer, NextPacketNumber(), r);
                length = Pad(buffer, length);
                await SendOnAnyAsync(buffer.AsSpan(0, length).ToArray(), length, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    private static int BuildRepair(Span<byte> b, ulong pn, FecEncoder.Repair r)
    {
        var w = new PacketWriter(b, pn);
        w.WriteFecRepair(r.BlockId, r.ParityCount, r.ShardIndex, r.ShardSize, r.Members, r.Parity);
        return w.Length;
    }

    private int Pad(byte[] buffer, int length)
    {
        if (!_shaper.Enabled) return length;
        int target = _shaper.Quantize(length);
        if (target > length)
        {
            Array.Clear(buffer, length, target - length);
            return target;
        }

        return length;
    }

    private async Task RetransmitLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(20, cancellationToken).ConfigureAwait(false);
                if (_unacked.IsEmpty) continue;

                long now = Environment.TickCount64;
                int rto = _cc.RtoMs;
                bool multipath = CarrierCount > 1;
                int timeouts = 0;
                foreach (var (_, f) in _unacked)
                {
                    // Несущая - TCP: пока она жива, всё в неё записанное будет доставлено.
                    // Дубль в ту же несущую - чистый вред (TCP-over-TCP: очередь растёт,
                    // RTT растёт, RTO срабатывает снова, окно режется вдвое). Поэтому:
                    //  - несущая пакета умерла -> переслать сразу (это не перегрузка);
                    //  - мультипуть и RTO истёк -> несущая, видимо, зажата: переслать
                    //    по ДРУГОЙ несущей, это потеря для congestion control;
                    //  - одна живая несущая -> ничего не делать, ждать ACK.
                    if (f.Sending) continue;
                    Carrier? via = f.Via;
                    bool carrierDead = via is null || !via.Alive;
                    bool timedOut = multipath && now - f.SentTicks >= rto;
                    if (!carrierDead && !timedOut) continue;

                    if (timedOut && !carrierDead) timeouts++;
                    f.SentTicks = now;
                    f.Retransmitted = true;
                    f.Via = await SendOnAnyAsync(f.Plaintext, f.Length, cancellationToken, avoid: via)
                        .ConfigureAwait(false);
                }

                if (timeouts > 0)
                {
                    _cc.OnLoss();
                    Interlocked.Add(ref _lossCountInterval, timeouts);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // ignored
        }
    }

    /// <summary>Коалесцирование ACK: не на каждый пакет, а не чаще раза в 5 мс.</summary>
    private async Task AckLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(5, cancellationToken).ConfigureAwait(false);
                if (Interlocked.Exchange(ref _ackPending, 0) == 1 && _ackVia is { } via)
                    await SendAckAsync(via, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // ignored
        }
    }

    /// <summary>Раз в интервал собирает сэмпл (RTT/доставка/потери) и переключает режим
    /// CongestionControl: при «полисинге» не режем скорость вдвое (искусственный потолок).</summary>
    private async Task PolicingLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                PurgeEarlyFrames();
                long delivered = Interlocked.Exchange(ref _deliveredBytesInterval, 0);
                long losses = Interlocked.Exchange(ref _lossCountInterval, 0);
                _detector.AddSample(Environment.TickCount64, _cc.SmoothedRttMs, delivered,
                    (int)Math.Min(int.MaxValue, losses));

                var v = _detector.Analyze();
                _cc.PolicingMode = v is { Cause: LossCause.Policing, Confidence: >= 0.5 };
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // ignored
        }
    }

    private void ProcessAck(AckFrame ack)
    {
        long now = Environment.TickCount64;
        foreach (var (lo, hi) in AckedRanges(ack))
        {
            // Каждый ACK повторяет всё окно приёма (до 4096 номеров), а в полёте обычно
            // гораздо меньше. Если диапазон шире словаря - дешевле пройти по словарю.
            if (hi - lo + 1 > (ulong)_unacked.Count)
            {
                foreach (var (pn, _) in _unacked)
                    if (pn >= lo && pn <= hi)
                        Acked(pn, now);
            }
            else
            {
                for (ulong pn = lo; pn <= hi && pn >= lo; pn++) Acked(pn, now);
            }
        }
    }

    private void Acked(ulong pn, long now)
    {
        if (!_unacked.TryRemove(pn, out var f)) return;
        Interlocked.Add(ref _deliveredBytesInterval, f.Length);
        _cc.OnAck(f.Length, (int)(now - f.SentTicks), f.Retransmitted || f.Sending);
    }

    private static IEnumerable<(ulong Lo, ulong Hi)> AckedRanges(AckFrame ack)
    {
        ulong high = ack.LargestAcked;
        if (ack.FirstRange > high) yield break;
        ulong prevLo = high - ack.FirstRange;
        yield return (prevLo, high);
        foreach (var r in ack.Ranges)
        {
            if (prevLo < r.Gap + 2) yield break;
            ulong h = prevLo - r.Gap - 2;
            if (r.Length > h) yield break;
            yield return (h - r.Length, h);
            prevLo = h - r.Length;
        }
    }

    private async ValueTask SendAckAsync(Carrier via, CancellationToken cancellationToken)
    {
        (ulong largest, ulong firstRange, List<AckRange> ranges) = BuildAckRanges();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(512);
        try
        {
            int length = BuildAck(buffer, NextPacketNumber(), largest, firstRange, ranges);
            length = Pad(buffer, length);
            if (via.Alive)
                await via.Channel.WriteRecordAsync(buffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
            Interlocked.Add(ref _bytesSent, length);
        }
        catch (Exception)
        {
            via.Alive = false;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static int BuildAck(Span<byte> buffer, ulong pn, ulong largest, ulong firstRange, List<AckRange> ranges)
    {
        var writer = new PacketWriter(buffer, pn);
        writer.WriteAck(largest, 0, firstRange, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(ranges));
        return writer.Length;
    }

    private (ulong Largest, ulong FirstRange, List<AckRange> Ranges) BuildAckRanges()
    {
        lock (_receivedLock)
        {
            var runs = new List<(ulong Lo, ulong Hi)>(4);
            bool started = false;
            ulong lo = 0, hi = 0;
            foreach (ulong pn in _received)
            {
                if (!started)
                {
                    lo = hi = pn;
                    started = true;
                }
                else if (pn == hi + 1) hi = pn;
                else
                {
                    runs.Add((lo, hi));
                    lo = hi = pn;
                }
            }

            runs.Add((lo, hi));

            var top = runs[^1];
            ulong largest = top.Hi, firstRange = top.Hi - top.Lo, prevLo = top.Lo;
            var ranges = new List<AckRange>();
            for (int i = runs.Count - 2; i >= 0; i--)
            {
                var r = runs[i];
                ranges.Add(new AckRange(prevLo - r.Hi - 2, r.Hi - r.Lo));
                prevLo = r.Lo;
            }

            return (largest, firstRange, ranges);
        }
    }

    private async Task ReceiveLoopAsync(Carrier carrier, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[Crypto.RecordFormat.MaxPlaintext];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                int n = await carrier.Channel.ReadRecordAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (n < 0) break;
                Interlocked.Add(ref _bytesReceived, n);
                await HandleIncomingAsync(buffer.AsMemory(0, n), carrier, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // ignored
        }
        finally
        {
            carrier.Alive = false;
            if (CarrierCount == 0) OnAllCarriersDead();
        }
    }

    /// <summary>
    /// Все несущие мертвы - сессия не может ни отправить, ни получить. Будим всех,
    /// кто ждёт: отправителей в окне и читателей потоков (раньше они висели вечно).
    /// </summary>
    private void OnAllCarriersDead()
    {
        var error = new IOException("Все несущие сессии закрыты");
        _cc.Abort(error);
        foreach (var st in _streams.Values) st.CompleteInbound(error);
        _completed.TrySetResult();
    }

    /// <summary>
    /// Обрабатывает один расшифрованный пакет (из несущей или восстановленный FEC):
    /// дедуп по номеру, разбор, диспатч фреймов, планирование ACK. FEC-repair фреймы
    /// пытаются восстановить потерянные пакеты блока - восстановленные прогоняются
    /// через этот же метод (дедуп не даст обработать дважды, если пакет позже доедет).
    /// </summary>
    private async ValueTask HandleIncomingAsync(ReadOnlyMemory<byte> record, Carrier? via,
        CancellationToken cancellationToken, bool fromFec = false)
    {
        var frames = new List<Frame>();
        ulong pn;
        try
        {
            pn = PacketReader.Parse(record, frames);
        }
        catch (ChameleonProtocolException)
        {
            return;
        }
        
        if (_fecDec is not null && CarrierCount > 1) _fecDec.Remember(pn, record.Span);

        bool reliable = false, isNew;
        lock (_receivedLock)
        {
            isNew = _received.Add(pn);
            if (_received.Count > MaxTrackedReceived) _received.Remove(_received.Min);
        }

        if (isNew)
        {
            if (fromFec) Interlocked.Increment(ref _fecRecovered);
            foreach (var frame in frames)
            {
                reliable |= frame is StreamOpenFrame or StreamFrame or StreamFinFrame or StreamResetFrame;
                if (frame is FecRepairFrame repair)
                {
                    if (_fecDec is not null)
                        foreach (var recovered in _fecDec.OnRepair(repair))
                            await HandleIncomingAsync(recovered, via, cancellationToken, fromFec: true)
                                .ConfigureAwait(false);
                }
                else
                {
                    await DispatchAsync(frame, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        else
        {
            reliable = frames.Any(f => f is StreamOpenFrame or StreamFrame or StreamFinFrame or StreamResetFrame);
        }

        if (reliable && via is not null)
        {
            _ackVia = via;
            Interlocked.Exchange(ref _ackPending, 1);
        }
    }

    private async ValueTask DispatchAsync(Frame frame, CancellationToken cancellationToken)
    {
        switch (frame)
        {
            case AckFrame ack:
                ProcessAck(ack);
                break;
            case StreamOpenFrame open:
            {
                var s = new ChameleonStream(this, open.StreamId, open.Kind, open.Host, open.Port);
                List<Frame>? early;
                lock (_early)
                {
                    if (_closedPeerStreams.ContainsKey(open.StreamId)) break; // поздний дубль OPEN
                    if (!_streams.TryAdd(open.StreamId, s)) break;
                    _early.Remove(open.StreamId, out var e);
                    early = e.Frames;
                }

                StreamAccepted?.Invoke(s);
                if (early is not null)
                    foreach (var f in early)
                        await DispatchAsync(f, cancellationToken).ConfigureAwait(false);
                break;
            }
            case StreamFrame data:
                if (TryGetStreamOrPark(data.StreamId, data, out var ds))
                    await ds.ReceiveDataAsync(data.Offset, data.Data, cancellationToken).ConfigureAwait(false);
                break;
            case StreamFinFrame fin:
                if (TryGetStreamOrPark(fin.StreamId, fin, out var fs))
                    await fs.ReceiveFinAsync(fin.FinalOffset, cancellationToken).ConfigureAwait(false);
                break;
            case StreamResetFrame reset:
                if (_streams.TryGetValue(reset.StreamId, out var rs))
                    rs.CompleteInbound(new IOException($"Поток сброшен, код {reset.ErrorCode}"));
                ReleaseStream(reset.StreamId); // и RESET, обогнавший OPEN, не даст потоку подняться
                break;
            default:
                break;
        }
    }

    /// <summary>
    /// В мультипути STREAM/FIN могут обогнать свой STREAM_OPEN (он поехал по другой
    /// несущей). Раньше такие данные молча выбрасывались, но подтверждались ACK'ом -
    /// дыра навсегда, поток вис. Теперь они паркуются до прихода OPEN.
    /// </summary>
    private bool TryGetStreamOrPark(ulong streamId, Frame frame, out ChameleonStream stream)
    {
        if (_streams.TryGetValue(streamId, out stream!)) return true;

        if (!IsPeerStream(streamId)) return false; // наш поток, уже закрытый - поздний дубль

        // Данные указывают в буфер приёма несущей, который переиспользуется - копируем.
        if (frame is StreamFrame sf) frame = sf with { Data = sf.Data.ToArray() };
        lock (_early)
        {
            if (_streams.TryGetValue(streamId, out stream!)) return true; // OPEN успел между проверками
            if (_closedPeerStreams.ContainsKey(streamId)) return false;   // поздний дубль закрытого
            if (!_early.TryGetValue(streamId, out var e))
            {
                if (_early.Count >= MaxEarlyStreams) return false;
                e = (Environment.TickCount64, []);
                _early[streamId] = e;
            }

            if (e.Frames.Count < MaxEarlyFramesPerStream) e.Frames.Add(frame);
        }

        return false;
    }

    /// <summary>Выбросить припаркованные фреймы, чей OPEN так и не пришёл (поздние дубли закрытых потоков).</summary>
    private void PurgeEarlyFrames()
    {
        long now = Environment.TickCount64;
        lock (_early)
        {
            foreach (var (id, e) in _early)
                if (now - e.SinceTicks > EarlyFrameTtlMs)
                    _early.Remove(id);
            foreach (var (id, since) in _closedPeerStreams)
                if (now - since > ClosedStreamTtlMs)
                    _closedPeerStreams.Remove(id);
        }
    }

    private async Task CoverLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                (TimeSpan delay, int size) = _shaper.NextCover();
                long mark = Interlocked.Read(ref _lastSendTicks);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                if (Interlocked.Read(ref _lastSendTicks) != mark) continue;

                byte[] buffer = ArrayPool<byte>.Shared.Rent(Crypto.RecordFormat.MaxPlaintext);
                try
                {
                    int length = BuildCover(buffer, NextPacketNumber(), size);
                    await SendOnAnyAsync(buffer.AsSpan(0, length).ToArray(), length, cancellationToken)
                        .ConfigureAwait(false);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // ignored
        }
    }

    private ulong NextPacketNumber() => (ulong)Interlocked.Increment(ref _nextPacketNumber) - 1;

    private static int BuildStreamOpen(Span<byte> b, ulong pn, ulong id, StreamKind kind, string host, ushort port)
    {
        var w = new PacketWriter(b, pn);
        w.WriteStreamOpen(id, kind, host, port);
        return w.Length;
    }

    private static int BuildStreamData(Span<byte> b, ulong pn, ulong id, ulong offset, ReadOnlySpan<byte> data)
    {
        var w = new PacketWriter(b, pn);
        w.WriteStream(id, offset, data);
        return w.Length;
    }

    private static int BuildStreamFin(Span<byte> b, ulong pn, ulong id, ulong finalOffset)
    {
        var w = new PacketWriter(b, pn);
        w.WriteStreamFin(id, finalOffset);
        return w.Length;
    }

    private static int BuildStreamReset(Span<byte> b, ulong pn, ulong id, ulong errorCode)
    {
        var w = new PacketWriter(b, pn);
        w.WriteStreamReset(id, errorCode);
        return w.Length;
    }

    private static int BuildCover(Span<byte> b, ulong pn, int targetSize)
    {
        var w = new PacketWriter(b, pn);
        int len = w.Length;
        if (targetSize > len)
        {
            b.Slice(len, targetSize - len).Clear();
            return targetSize;
        }

        return len;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _cc.Abort(new ObjectDisposedException(nameof(ChameleonSession)));
        Carrier[] carriers;
        lock (_carriers) carriers = [.. _carriers];
        foreach (var c in carriers)
        {
            try
            {
                await c.Channel.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // ignored
            }
        }

        foreach (var t in new[] { _coverLoop, _rtoLoop, _ackLoop, _policingLoop }.Concat(carriers.Select(c => c.Loop)))
            if (t is not null)
            {
                try
                {
                    await t.ConfigureAwait(false);
                }
                catch
                {
                    // ignored
                }
            }

        foreach (var st in _streams.Values) st.CompleteInbound();
        _cts.Dispose();
    }
}