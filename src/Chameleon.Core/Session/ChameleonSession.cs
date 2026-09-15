using System.Buffers;
using System.Collections.Concurrent;
using Chameleon.Core.Protocol;
using Chameleon.Core.Transport;

namespace Chameleon.Core.Session;

/// <summary>
/// Логическая сессия поверх одной несущей (<see cref="RecordChannel"/>).
/// Мультиплексирует много потоков пользователя в один зашифрованный канал.
///
/// Все исходящие record'ы проходят через <see cref="EmitAsync"/>, где
/// применяется шейпер (<see cref="TrafficShaper"/>): квантование размеров и
/// прикрытие в простое.
/// </summary>
public sealed class ChameleonSession : IAsyncDisposable
{
    private readonly RecordChannel _channel;
    private readonly bool _isClient;
    private readonly TrafficShaper _shaper;
    private readonly ConcurrentDictionary<ulong, ChameleonStream> _streams = new();
    private readonly CancellationTokenSource _cts = new();

    private long _nextPacketNumber;
    private long _nextStreamId;
    private long _lastSendTicks;
    private readonly int _maxStreamChunk;
    private Task? _receiveLoop;
    private Task? _coverLoop;

    private ChameleonSession(RecordChannel channel, bool isClient, TrafficShaper shaper)
    {
        _channel = channel;
        _isClient = isClient;
        _shaper = shaper;
        _nextStreamId = isClient ? 1 : 2;
        _lastSendTicks = Environment.TickCount64;

        int cap = Crypto.RecordFormat.MaxPlaintext - 64;
        _maxStreamChunk = shaper.Enabled ? Math.Min(cap, shaper.LargestSize - 64) : cap;
    }

    /// <summary>Новый поток, открытый удалённой стороной (актуально для сервера).</summary>
    public Action<ChameleonStream>? StreamAccepted { get; set; }

    public static ChameleonSession Start(RecordChannel channel, bool isClient, TrafficShaper? shaper = null)
    {
        var session = new ChameleonSession(channel, isClient, shaper ?? TrafficShaper.Off);
        session._receiveLoop = Task.Run(() => session.ReceiveLoopAsync(session._cts.Token));
        if (session._shaper.Enabled && session._shaper.IdleCoverInterval > TimeSpan.Zero)
            session._coverLoop = Task.Run(() => session.CoverLoopAsync(session._cts.Token));
        return session;
    }

    public async ValueTask<ChameleonStream> OpenStreamAsync(
        string host, int port, StreamKind kind = StreamKind.Tcp, CancellationToken cancellationToken = default)
    {
        ulong id = (ulong)Interlocked.Add(ref _nextStreamId, 2) - 2;
        var stream = new ChameleonStream(this, id, kind, host, port);
        _streams[id] = stream;

        await SendBuiltAsync(Crypto.RecordFormat.MaxPlaintext,
            buffer => BuildStreamOpen(buffer, NextPacketNumber(), id, kind, host, (ushort)port),
            cancellationToken).ConfigureAwait(false);

        return stream;
    }

    internal async ValueTask SendStreamDataAsync(
        ulong streamId, ulong offset, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        int sent = 0;
        while (sent < data.Length)
        {
            int chunk = Math.Min(_maxStreamChunk, data.Length - sent);
            ReadOnlyMemory<byte> slice = data.Slice(sent, chunk);
            await SendBuiltAsync(Crypto.RecordFormat.MaxPlaintext,
                buffer => BuildStreamData(buffer, NextPacketNumber(), streamId, offset + (ulong)sent, slice.Span),
                cancellationToken).ConfigureAwait(false);
            sent += chunk;
        }
    }

    internal ValueTask SendStreamFinAsync(ulong streamId, ulong finalOffset, CancellationToken cancellationToken)
        => SendBuiltAsync(Crypto.RecordFormat.MaxPlaintext,
            buffer => BuildStreamFin(buffer, NextPacketNumber(), streamId, finalOffset),
            cancellationToken);

    public ValueTask ResetStreamAsync(ulong streamId, ulong errorCode = 0,
        CancellationToken cancellationToken = default)
    {
        _streams.TryRemove(streamId, out _);
        return SendBuiltAsync(Crypto.RecordFormat.MaxPlaintext,
            buffer => BuildStreamReset(buffer, NextPacketNumber(), streamId, errorCode),
            cancellationToken);
    }

    /// <summary>Собирает пакет в аренду из пула и отправляет через шейпер.</summary>
    private async ValueTask SendBuiltAsync(int rentSize, Func<byte[], int> build, CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(rentSize);
        try
        {
            int length = build(buffer);
            await EmitAsync(buffer, length, targetSize: null, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Единственная точка записи record'а. Дополняет пакет PADDING'ом до размера,
    /// который выбирает шейпер (квантование), и обновляет метку последней отправки.
    /// </summary>
    private async ValueTask EmitAsync(byte[] buffer, int length, int? targetSize, CancellationToken cancellationToken)
    {
        if (_shaper.Enabled)
        {
            int target = targetSize ?? _shaper.Quantize(length);
            if (target > length)
            {
                Array.Clear(buffer, length, target - length);
                length = target;
            }
        }

        Interlocked.Exchange(ref _lastSendTicks, Environment.TickCount64);
        await _channel.WriteRecordAsync(buffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
    }

    private async Task CoverLoopAsync(CancellationToken cancellationToken)
    {
        TimeSpan interval = _shaper.IdleCoverInterval;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);

                long idleMs = Environment.TickCount64 - Interlocked.Read(ref _lastSendTicks);
                if (idleMs < interval.TotalMilliseconds) continue;

                byte[] buffer = ArrayPool<byte>.Shared.Rent(Crypto.RecordFormat.MaxPlaintext);
                try
                {
                    int length = BuildCover(buffer, NextPacketNumber());
                    await EmitAsync(buffer, length, targetSize: _shaper.RandomCoverSize(), cancellationToken)
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

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[Crypto.RecordFormat.MaxPlaintext];
        var frames = new List<Frame>();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                int n = await _channel.ReadRecordAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (n < 0) break;

                frames.Clear();
                PacketReader.Parse(buffer.AsMemory(0, n), frames); // PADDING-пакеты прикрытия дают 0 фреймов
                foreach (var frame in frames)
                    await DispatchAsync(frame, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            FailAllStreams(error);
            return;
        }

        FailAllStreams(null);
    }

    private async ValueTask DispatchAsync(Frame frame, CancellationToken cancellationToken)
    {
        switch (frame)
        {
            case StreamOpenFrame open:
            {
                var stream = new ChameleonStream(this, open.StreamId, open.Kind, open.Host, open.Port);
                if (_streams.TryAdd(open.StreamId, stream))
                    StreamAccepted?.Invoke(stream);
                break;
            }
            case StreamFrame data when _streams.TryGetValue(data.StreamId, out var stream):
                await stream.ReceiveDataAsync(data.Data, cancellationToken).ConfigureAwait(false);
                break;
            case StreamFinFrame fin when _streams.TryGetValue(fin.StreamId, out var stream):
                stream.CompleteInbound();
                break;
            case StreamResetFrame reset when _streams.TryRemove(reset.StreamId, out var stream):
                stream.CompleteInbound(new IOException($"Поток сброшен, код {reset.ErrorCode}"));
                break;
            default:
                break;
        }
    }

    private void FailAllStreams(Exception? error)
    {
        foreach (var stream in _streams.Values)
            stream.CompleteInbound(error);
        _streams.Clear();
    }

    private ulong NextPacketNumber() => (ulong)Interlocked.Increment(ref _nextPacketNumber) - 1;
    
    private static int BuildStreamOpen(Span<byte> buffer, ulong pn, ulong id, StreamKind kind, string host, ushort port)
    {
        var writer = new PacketWriter(buffer, pn);
        writer.WriteStreamOpen(id, kind, host, port);
        return writer.Length;
    }

    private static int BuildStreamData(Span<byte> buffer, ulong pn, ulong id, ulong offset, ReadOnlySpan<byte> data)
    {
        var writer = new PacketWriter(buffer, pn);
        writer.WriteStream(id, offset, data);
        return writer.Length;
    }

    private static int BuildStreamFin(Span<byte> buffer, ulong pn, ulong id, ulong finalOffset)
    {
        var writer = new PacketWriter(buffer, pn);
        writer.WriteStreamFin(id, finalOffset);
        return writer.Length;
    }

    private static int BuildStreamReset(Span<byte> buffer, ulong pn, ulong id, ulong errorCode)
    {
        var writer = new PacketWriter(buffer, pn);
        writer.WriteStreamReset(id, errorCode);
        return writer.Length;
    }

    private static int BuildCover(Span<byte> buffer, ulong pn)
    {
        var writer = new PacketWriter(buffer, pn);
        return writer.Length;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        foreach (var task in new[] { _receiveLoop, _coverLoop })
        {
            if (task is not null)
            {
                try
                {
                    await task.ConfigureAwait(false);
                }
                catch
                {
                    // ignored
                }
            }
        }

        await _channel.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
    }
}