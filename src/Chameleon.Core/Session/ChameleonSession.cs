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
    public static readonly FecOptions Default = new();
    public static readonly FecOptions Off = new(8, 2, Enabled: false);
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
    }

    private sealed record InFlight(byte[] Plaintext, int Length, long SentTicks, bool Retransmitted);

    private readonly List<Carrier> _carriers = [];
    private readonly bool _isClient;
    private readonly TrafficShaper _shaper;
    private readonly int _maxStreamChunk;

    private readonly ConcurrentDictionary<ulong, ChameleonStream> _streams = new();
    private readonly ConcurrentDictionary<ulong, InFlight> _unacked = new();
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
    public int CarrierCount => _carriers.Count(c => c.Alive);
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
        _carriers.Add(c);
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
        _streams.TryRemove(streamId, out _);
        return SendReliableAsync(b => BuildStreamReset(b, NextPacketNumber(), streamId, errorCode), cancellationToken);
    }

    /// <summary>Отправляет пакет и запоминает его для ретрансмита, пока не придёт ACK.</summary>
    private async ValueTask SendReliableAsync(Func<byte[], int> build, CancellationToken cancellationToken)
    {
        byte[] scratch = ArrayPool<byte>.Shared.Rent(Crypto.RecordFormat.MaxPlaintext);
        try
        {
            int rawLen = build(scratch);
            int length = Pad(scratch, rawLen);
            
            VarInt.TryRead(scratch, out ulong pn, out _);
            byte[] plaintext = [.. scratch[..length]];
            
            await _cc.AcquireAsync(cancellationToken).ConfigureAwait(false);
            _unacked[pn] = new InFlight(plaintext, length, Environment.TickCount64, Retransmitted: false);

            await SendOnAnyAsync(plaintext, length, cancellationToken).ConfigureAwait(false);
            
            if (_fec is not null)
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

    private async ValueTask SendOnAnyAsync(byte[] plaintext, int length, CancellationToken cancellationToken)
    {
        int n = _carriers.Count;
        for (int attempt = 0; attempt < n; attempt++)
        {
            int idx = (Interlocked.Increment(ref _rrIndex) & int.MaxValue) % n;
            Carrier c = _carriers[idx];
            if (!c.Alive) continue;
            try
            {
                await c.Channel.WriteRecordAsync(plaintext.AsMemory(0, length), cancellationToken)
                    .ConfigureAwait(false);
                Interlocked.Add(ref _bytesSent, length);
                Interlocked.Exchange(ref _lastSendTicks, Environment.TickCount64);
                return;
            }
            catch (Exception)
            {
                c.Alive = false;
            }
        }
    }

    /// <summary>Шлёт repair-шарды блока. Только при мультипути: на одной надёжной несущей
    /// потерь нет, parity был бы чистым оверхедом. Repair-пакеты ненадёжны (не ретрансмитятся).</summary>
    private async ValueTask SendRepairsAsync(FecEncoder.Repair[] repairs, CancellationToken cancellationToken)
    {
        if (_carriers.Count(c => c.Alive) < 2) return;
        foreach (var r in repairs)
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(Crypto.RecordFormat.MaxPlaintext);
            try
            {
                int length = BuildRepair(buffer, NextPacketNumber(), r);
                length = Pad(buffer, length);
                await SendOnAnyAsync(buffer[..length].ToArray(), length, cancellationToken).ConfigureAwait(false);
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
                long now = Environment.TickCount64;
                int rto = _cc.RtoMs;
                bool loss = false;
                int retransmits = 0;
                foreach (var (pn, f) in _unacked)
                {
                    if (now - f.SentTicks < rto) continue;
                    loss = true;
                    retransmits++;
                    _unacked[pn] = f with { SentTicks = now, Retransmitted = true };
                    await SendOnAnyAsync(f.Plaintext, f.Length, cancellationToken).ConfigureAwait(false);
                }

                if (loss)
                {
                    _cc.OnLoss();
                    Interlocked.Add(ref _lossCountInterval, retransmits);
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
        foreach (ulong pn in AckedPacketNumbers(ack))
            if (_unacked.TryRemove(pn, out var f))
            {
                Interlocked.Add(ref _deliveredBytesInterval, f.Length);
                _cc.OnAck((int)(now - f.SentTicks), f.Retransmitted);
            }
    }

    private static IEnumerable<ulong> AckedPacketNumbers(AckFrame ack)
    {
        ulong high = ack.LargestAcked;
        for (ulong p = high - ack.FirstRange; p <= high; p++) yield return p;
        ulong prevLo = high - ack.FirstRange;
        foreach (var r in ack.Ranges)
        {
            if (prevLo < r.Gap + 2) yield break;
            ulong h = prevLo - r.Gap - 2;
            for (ulong p = h - r.Length; p <= h; p++) yield return p;
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
            if (_carriers.All(c => !c.Alive)) _completed.TrySetResult();
        }
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
                var s = new ChameleonStream(this, open.StreamId, open.Kind, open.Host, open.Port);
                if (_streams.TryAdd(open.StreamId, s)) StreamAccepted?.Invoke(s);
                break;
            case StreamFrame data when _streams.TryGetValue(data.StreamId, out var st):
                await st.ReceiveDataAsync(data.Offset, data.Data, cancellationToken).ConfigureAwait(false);
                break;
            case StreamFinFrame fin when _streams.TryGetValue(fin.StreamId, out var st):
                await st.ReceiveFinAsync(fin.FinalOffset, cancellationToken).ConfigureAwait(false);
                break;
            case StreamResetFrame reset when _streams.TryRemove(reset.StreamId, out var st):
                st.CompleteInbound(new IOException($"Поток сброшен, код {reset.ErrorCode}"));
                break;
            default:
                break;
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
                    await SendOnAnyAsync([.. buffer[..length]], length, cancellationToken).ConfigureAwait(false);
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
        foreach (var c in _carriers)
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

        foreach (var t in new[] { _coverLoop, _rtoLoop, _ackLoop, _policingLoop }.Concat(_carriers.Select(c => c.Loop)))
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