using System.Buffers;
using System.Collections.Concurrent;
using Chameleon.Core.Protocol;
using Chameleon.Core.Transport;

namespace Chameleon.Core.Session;

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
    private static readonly TimeSpan RetransmitTimeout = TimeSpan.FromMilliseconds(300);
    private const int MaxTrackedReceived = 4096;

    private sealed class Carrier(RecordChannel channel)
    {
        public RecordChannel Channel { get; } = channel;
        public volatile bool Alive = true;
        public Task? Loop;
    }

    private sealed record InFlight(byte[] Plaintext, int Length, long SentTicks);

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
    private int _rrIndex;
    private Task? _coverLoop;
    private Task? _rtoLoop;

    private ChameleonSession(RecordChannel first, bool isClient, TrafficShaper shaper)
    {
        _isClient = isClient;
        _shaper = shaper;
        _nextStreamId = isClient ? 1 : 2;
        _lastSendTicks = Environment.TickCount64;
        int cap = Crypto.RecordFormat.MaxPlaintext - 64;
        _maxStreamChunk = shaper.Enabled ? Math.Min(cap, shaper.LargestSize - 64) : cap;
        _carriers.Add(new Carrier(first));
    }

    public Action<ChameleonStream>? StreamAccepted { get; set; }
    public int CarrierCount => _carriers.Count(c => c.Alive);

    public static ChameleonSession Start(RecordChannel channel, bool isClient, TrafficShaper? shaper = null)
    {
        var s = new ChameleonSession(channel, isClient, shaper ?? TrafficShaper.Off);
        s.StartCarrierLoop(s._carriers[0]);
        if (s._shaper.CoverActive)
            s._coverLoop = Task.Run(() => s.CoverLoopAsync(s._cts.Token));
        s._rtoLoop = Task.Run(() => s.RetransmitLoopAsync(s._cts.Token));
        return s;
    }

    /// <summary>Добавить ещё одну несущую в живую сессию (ключи выведены для её carrier_id).</summary>
    public void AddCarrier(RecordChannel channel)
    {
        var c = new Carrier(channel);
        _carriers.Add(c);
        StartCarrierLoop(c);
    }

    private void StartCarrierLoop(Carrier c) => c.Loop = Task.Run(() => ReceiveLoopAsync(c, _cts.Token));

    // --- открытие потоков и отправка ---

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
            int length = build(scratch);
            length = Pad(scratch, length);

            VarInt.TryRead(scratch, out ulong pn, out _);
            byte[] plaintext = scratch[..length].ToArray();
            _unacked[pn] = new InFlight(plaintext, length, Environment.TickCount64);

            await SendOnAnyAsync(plaintext, length, cancellationToken).ConfigureAwait(false);
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
                Interlocked.Exchange(ref _lastSendTicks, Environment.TickCount64);
                return;
            }
            catch (Exception)
            {
                c.Alive = false;
            }
        }
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

    // --- надёжность: ретрансмиты и ACK ---

    private async Task RetransmitLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                long now = Environment.TickCount64;
                foreach (var (pn, f) in _unacked)
                {
                    if (now - f.SentTicks < RetransmitTimeout.TotalMilliseconds) continue;
                    _unacked[pn] = f with { SentTicks = now };
                    await SendOnAnyAsync(f.Plaintext, f.Length, cancellationToken).ConfigureAwait(false);
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

    private void ProcessAck(AckFrame ack)
    {
        foreach (ulong pn in AckedPacketNumbers(ack))
            _unacked.TryRemove(pn, out _);
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
            ulong[] arr = [.. _received];
            var runs = new List<(ulong Lo, ulong Hi)>();
            ulong lo = arr[0], hi = arr[0];
            for (int i = 1; i < arr.Length; i++)
            {
                if (arr[i] == hi + 1) hi = arr[i];
                else
                {
                    runs.Add((lo, hi));
                    lo = hi = arr[i];
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

    // --- приём ---

    private async Task ReceiveLoopAsync(Carrier carrier, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[Crypto.RecordFormat.MaxPlaintext];
        var frames = new List<Frame>();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                int n = await carrier.Channel.ReadRecordAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (n < 0) break;

                frames.Clear();
                ulong pn = PacketReader.Parse(buffer.AsMemory(0, n), frames);

                bool reliable = false, isNew;
                lock (_receivedLock)
                {
                    isNew = _received.Add(pn);
                    if (_received.Count > MaxTrackedReceived) _received.Remove(_received.Min);
                }

                if (isNew)
                {
                    foreach (var frame in frames)
                    {
                        reliable |= frame is StreamOpenFrame or StreamFrame or StreamFinFrame or StreamResetFrame;
                        await DispatchAsync(frame, cancellationToken).ConfigureAwait(false);
                    }
                }
                else
                {
                    reliable =
                        frames.Any(f => f is StreamOpenFrame or StreamFrame or StreamFinFrame or StreamResetFrame);
                }

                if (reliable)
                    await SendAckAsync(carrier, cancellationToken).ConfigureAwait(false);
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
                    await SendOnAnyAsync(buffer[..length].ToArray(), length, cancellationToken).ConfigureAwait(false);
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
        foreach (var t in new[] { _coverLoop, _rtoLoop }.Concat(_carriers.Select(c => c.Loop)))
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
        foreach (var c in _carriers) await c.Channel.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
    }
}