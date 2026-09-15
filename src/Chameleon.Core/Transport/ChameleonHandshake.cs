using System.Buffers.Binary;
using Chameleon.Core.Crypto;

namespace Chameleon.Core.Transport;

/// <summary>
/// Проводит рукопожатие Noise IK поверх Stream и отдаёт готовый <see cref="RecordChannel"/>.
///
/// Сейчас сообщения идут по «сырому» Stream с 2-байтным префиксом длины. На этапе
/// TLS-несущей рукопожатие переедет ВНУТРЬ TLS, а сервер при неудачном рукопожатии
/// будет отвечать как настоящий веб-сервер (сейчас - просто закрывает соединение,
/// ничего не выдавая зонду).
/// </summary>
public static class ChameleonHandshake
{
    public static async Task<RecordChannel> ConnectAsync(
        Stream stream, KeyPair clientStatic, byte[] serverStaticPublic, uint carrierId,
        CancellationToken cancellationToken = default)
    {
        var handshake = NoiseIkHandshake.CreateInitiator(clientStatic, serverStaticPublic);

        await WriteFrameAsync(stream, handshake.WriteMessage1(), cancellationToken).ConfigureAwait(false);
        byte[] message2 = await ReadFrameAsync(stream, cancellationToken).ConfigureAwait(false);
        HandshakeResult result = handshake.ReadMessage2(message2);

        var keys = KeySchedule.ForCarrier(result.SessionSecret, carrierId, isClient: true);
        return new RecordChannel(stream, keys);
    }

    /// <returns>Канал и статический публичный ключ клиента (его личность).</returns>
    public static async Task<(RecordChannel Channel, byte[] ClientStaticPublic)> AcceptAsync(
        Stream stream, KeyPair serverStatic, uint carrierId,
        CancellationToken cancellationToken = default)
    {
        var handshake = NoiseIkHandshake.CreateResponder(serverStatic);

        byte[] message1 = await ReadFrameAsync(stream, cancellationToken).ConfigureAwait(false);
        handshake.ReadMessage1(message1); // бросит при мусоре/зонде - вызывающий закроет соединение
        HandshakeResult result = handshake.WriteMessage2(out byte[] message2);
        await WriteFrameAsync(stream, message2, cancellationToken).ConfigureAwait(false);

        var keys = KeySchedule.ForCarrier(result.SessionSecret, carrierId, isClient: false);
        return (new RecordChannel(stream, keys), result.RemoteStaticPublic);
    }

    private static async Task WriteFrameAsync(Stream stream, byte[] message, CancellationToken cancellationToken)
    {
        byte[] header = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(header, (ushort)message.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(message, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] header = new byte[2];
        await ReadExactAsync(stream, header, cancellationToken).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadUInt16BigEndian(header);

        byte[] message = new byte[length];
        await ReadExactAsync(stream, message, cancellationToken).ConfigureAwait(false);
        return message;
    }

    private static async Task ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
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