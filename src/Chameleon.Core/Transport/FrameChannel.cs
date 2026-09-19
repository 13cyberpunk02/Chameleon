using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;

namespace Chameleon.Core.Transport;

/// <summary>
/// Несущая БЕЗ собственного шифрования: конфиденциальность и целостность даёт
/// TLS снаружи. На проводе внутри TLS: len(2, BE) ‖ packet(len). Это убирает
/// двойное шифрование (TLS-in-TLS) - главный оверхед по CPU.
///
/// Контракт совпадает с RecordChannel (<see cref="ICarrierChannel"/>), поэтому
/// сессия работает поверх него без изменений.
/// </summary>
public sealed class FrameChannel(Stream stream) : ICarrierChannel
{
    public const int HeaderSize = 2;
    public const int MaxPacket = 65535;

    private readonly PipeReader _reader = PipeReader.Create(stream, new StreamPipeReaderOptions(leaveOpen: true));
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public async ValueTask<int> ReadRecordAsync(Memory<byte> destination, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            ReadResult result = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            ReadOnlySequence<byte> buffer = result.Buffer;

            if (TryReadFrame(ref buffer, destination.Span, out int written))
            {
                _reader.AdvanceTo(buffer.Start);
                return written;
            }

            if (result.IsCompleted)
            {
                bool clean = buffer.IsEmpty;
                _reader.AdvanceTo(buffer.Start, buffer.End);
                return clean ? -1 : throw new ChameleonProtocolException("Поток оборван посреди кадра");
            }

            _reader.AdvanceTo(buffer.Start, buffer.End);
        }
    }

    public async ValueTask WriteRecordAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken = default)
    {
        if (packet.Length > MaxPacket)
            throw new ArgumentException("Пакет больше максимума кадра", nameof(packet));

        byte[] frame = ArrayPool<byte>.Shared.Rent(HeaderSize + packet.Length);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            BinaryPrimitives.WriteUInt16BigEndian(frame, (ushort)packet.Length);
            packet.Span.CopyTo(frame.AsSpan(HeaderSize));
            await stream.WriteAsync(frame.AsMemory(0, HeaderSize + packet.Length), cancellationToken)
                .ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
            ArrayPool<byte>.Shared.Return(frame);
        }
    }

    private static bool TryReadFrame(ref ReadOnlySequence<byte> buffer, Span<byte> destination, out int written)
    {
        written = 0;
        if (buffer.Length < HeaderSize) return false;

        Span<byte> header = stackalloc byte[HeaderSize];
        buffer.Slice(0, HeaderSize).CopyTo(header);
        int length = BinaryPrimitives.ReadUInt16BigEndian(header);

        if (buffer.Length < HeaderSize + length) return false;
        if (length > destination.Length)
            throw new ChameleonProtocolException("Кадр больше буфера получателя");

        buffer.Slice(HeaderSize, length).CopyTo(destination);
        written = length;
        buffer = buffer.Slice(HeaderSize + length);
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        await _reader.CompleteAsync().ConfigureAwait(false);
        await stream.DisposeAsync().ConfigureAwait(false);
        _writeLock.Dispose();
    }
}