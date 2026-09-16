using System.Buffers.Binary;

namespace Chameleon.Core.Transport;

/// <summary>Чтение/запись сообщений рукопожатия с 2-байтным префиксом длины.</summary>
public static class Framing
{
    public static async Task WriteFrameAsync(Stream stream, byte[] message, CancellationToken cancellationToken)
    {
        byte[] header = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(header, (ushort)message.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(message, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<int> ReadPrefixAsync(Stream stream, byte[] prefix, CancellationToken cancellationToken)
    {
        await ReadExactAsync(stream, prefix, cancellationToken).ConfigureAwait(false);
        return BinaryPrimitives.ReadUInt16BigEndian(prefix);
    }

    public static async Task<byte[]> ReadExactCountAsync(Stream stream, int count, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[count];
        await ReadExactAsync(stream, buffer, cancellationToken).ConfigureAwait(false);
        return buffer;
    }

    public static async Task ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer[read..], cancellationToken).ConfigureAwait(false);
            if (n == 0) throw new ChameleonProtocolException("Соединение закрыто во время рукопожатия");
            read += n;
        }
    }
}