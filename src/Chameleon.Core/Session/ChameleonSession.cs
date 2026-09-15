using System.Buffers;
using System.Collections.Concurrent;
using Chameleon.Core.Protocol;
using Chameleon.Core.Transport;

namespace Chameleon.Core.Session;

/// <summary>
/// Логическая сессия поверх одной несущей (<see cref="RecordChannel"/>).
/// Мультиплексирует много потоков пользователя в один зашифрованный канал,
/// раскладывает входящие record'ы на фреймы и разводит их по потокам.
///
/// Клиент открывает потоки через <see cref="OpenStreamAsync"/>; на сервере
/// каждый новый поток от клиента приходит в <see cref="StreamAccepted"/>.
/// </summary>
public sealed class ChameleonSession : IAsyncDisposable
{
    private const int MaxStreamChunk = Crypto.RecordFormat.MaxPlaintext - 64;

    private readonly RecordChannel _channel;
    private readonly bool _isClient;
    private readonly ConcurrentDictionary<ulong, ChameleonStream> _streams = new();
    private readonly CancellationTokenSource _cts = new();

    private long _nextPacketNumber;
    private long _nextStreamId;
    private Task? _receiveLoop;

    private ChameleonSession(RecordChannel channel, bool isClient)
    {
        _channel = channel;
        _isClient = isClient;
        _nextStreamId = isClient ? 1 : 2;
    }

    /// <summary>Новый поток, открытый удалённой стороной (актуально для сервера).</summary>
    public Action<ChameleonStream>? StreamAccepted { get; set; }

    public static ChameleonSession Start(RecordChannel channel, bool isClient)
    {
        var session = new ChameleonSession(channel, isClient);
        session._receiveLoop = Task.Run(() => session.ReceiveLoopAsync(session._cts.Token));
        return session;
    }

    public async ValueTask<ChameleonStream> OpenStreamAsync(
        string host, int port, StreamKind kind = StreamKind.Tcp, CancellationToken cancellationToken = default)
    {
        ulong id = (ulong)Interlocked.Add(ref _nextStreamId, 2) - 2;
        var stream = new ChameleonStream(this, id, kind, host, port);
        _streams[id] = stream;

        byte[] buffer = ArrayPool<byte>.Shared.Rent(Crypto.RecordFormat.MaxPlaintext);
        try
        {
            int length = BuildStreamOpen(buffer, NextPacketNumber(), id, kind, host, (ushort)port);
            await _channel.WriteRecordAsync(buffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return stream;
    }

    internal async ValueTask SendStreamDataAsync(
        ulong streamId, ulong offset, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        int sent = 0;
        while (sent < data.Length)
        {
            int chunk = Math.Min(MaxStreamChunk, data.Length - sent);
            byte[] buffer = ArrayPool<byte>.Shared.Rent(Crypto.RecordFormat.MaxPlaintext);
            try
            {
                int length = BuildStreamData(buffer, NextPacketNumber(), streamId,
                    offset + (ulong)sent, data.Slice(sent, chunk).Span);
                await _channel.WriteRecordAsync(buffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            sent += chunk;
        }
    }

    internal async ValueTask SendStreamFinAsync(ulong streamId, ulong finalOffset, CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64);
        try
        {
            int length = BuildStreamFin(buffer, NextPacketNumber(), streamId, finalOffset);
            await _channel.WriteRecordAsync(buffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public async ValueTask ResetStreamAsync(ulong streamId, ulong errorCode = 0,
        CancellationToken cancellationToken = default)
    {
        _streams.TryRemove(streamId, out _);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64);
        try
        {
            int length = BuildStreamReset(buffer, NextPacketNumber(), streamId, errorCode);
            await _channel.WriteRecordAsync(buffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
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
                PacketReader.Parse(buffer.AsMemory(0, n), frames);
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
            // ACK / PING / MAX_DATA / CLOSE - обработка появится с надёжной доставкой и мультипутём
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

    // синхронная сборка пакетов (PacketWriter - ref struct, вне async)

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

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_receiveLoop is not null)
        {
            try
            {
                await _receiveLoop.ConfigureAwait(false);
            }
            catch
            {
                /* останавливаемся */
            }
        }

        await _channel.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
    }
}