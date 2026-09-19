using System.Buffers;
using System.IO.Pipelines;
using Chameleon.Core.Crypto;

namespace Chameleon.Core.Transport;

/// <summary>
/// Одна несущая: превращает любой надёжный упорядоченный Stream (TCP, TLS, WebSocket)
/// в канал зашифрованных record'ов. Сессия выше не знает, какой Stream внизу.
/// </summary>
public sealed class RecordChannel(Stream stream, CarrierKeys keys) : ICarrierChannel
{
    private readonly PipeReader _reader = PipeReader.Create(stream, new StreamPipeReaderOptions(leaveOpen: true));
    private readonly RecordSealer _sealer = new(keys.Send);
    private readonly RecordOpener _opener = new(keys.Receive);
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <returns>Длина открытого текста или -1, если удалённая сторона корректно закрыла поток.</returns>
    public async ValueTask<int> ReadRecordAsync(Memory<byte> destination, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            ReadResult result = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            ReadOnlySequence<byte> buffer = result.Buffer;

            if (TryReadRecord(ref buffer, destination.Span, out int written))
            {
                _reader.AdvanceTo(buffer.Start);
                return written;
            }

            if (result.IsCompleted)
            {
                bool clean = buffer.IsEmpty;
                _reader.AdvanceTo(buffer.Start, buffer.End);
                return clean ? -1 : throw new ChameleonProtocolException("Поток оборван посреди record'а");
            }

            _reader.AdvanceTo(buffer.Start, buffer.End);
        }
    }

    public async ValueTask WriteRecordAsync(ReadOnlyMemory<byte> plaintext,
        CancellationToken cancellationToken = default)
    {
        byte[] record = ArrayPool<byte>.Shared.Rent(RecordFormat.Overhead + plaintext.Length);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int length = _sealer.Seal(plaintext.Span, record);
            await stream.WriteAsync(record.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
            ArrayPool<byte>.Shared.Return(record);
        }
    }

    private bool TryReadRecord(ref ReadOnlySequence<byte> buffer, Span<byte> destination, out int written)
    {
        written = 0;
        if (buffer.Length < RecordFormat.HeaderSize) return false;

        Span<byte> header = stackalloc byte[RecordFormat.HeaderSize];
        buffer.Slice(0, RecordFormat.HeaderSize).CopyTo(header);
        int bodyLength = _opener.PeekBodyLength(header);

        if (buffer.Length < RecordFormat.HeaderSize + bodyLength) return false;

        var body = buffer.Slice(RecordFormat.HeaderSize, bodyLength);
        if (body.IsSingleSegment)
        {
            written = _opener.Open(body.FirstSpan, destination);
        }
        else
        {
            byte[] rented = ArrayPool<byte>.Shared.Rent(bodyLength);
            try
            {
                body.CopyTo(rented);
                written = _opener.Open(rented.AsSpan(0, bodyLength), destination);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        buffer = buffer.Slice(RecordFormat.HeaderSize + bodyLength);
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        await _reader.CompleteAsync().ConfigureAwait(false);
        await stream.DisposeAsync().ConfigureAwait(false);
        _sealer.Dispose();
        _opener.Dispose();
        _writeLock.Dispose();
    }
}