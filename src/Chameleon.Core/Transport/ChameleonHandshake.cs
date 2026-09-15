using System.Buffers.Binary;
using Chameleon.Core.Crypto;

namespace Chameleon.Core.Transport;

/// <summary>
/// Итог попытки принять входящую несущую: либо аутентифицированный клиент
/// (<see cref="Channel"/> задан), либо чужак/зонд - тогда <see cref="Buffered"/>
/// содержит байты, уже прочитанные из потока, чтобы их можно было переиграть
/// декой-прокси (иначе реальный сайт получил бы обрезанный запрос).
/// </summary>
public sealed class AcceptOutcome
{
    private AcceptOutcome(RecordChannel? channel, byte[]? clientStaticPublic, byte[] buffered)
    {
        Channel = channel;
        ClientStaticPublic = clientStaticPublic;
        Buffered = buffered;
    }

    public RecordChannel? Channel { get; }
    public byte[]? ClientStaticPublic { get; }
    public byte[] Buffered { get; }
    public bool Succeeded => Channel is not null;

    internal static AcceptOutcome Success(RecordChannel channel, byte[] clientStaticPublic)
        => new(channel, clientStaticPublic, []);

    internal static AcceptOutcome Cover(byte[] buffered)
        => new(null, null, buffered);
}

/// <summary>
/// Проводит рукопожатие Noise IK поверх Stream (внутри TLS-несущей) и отдаёт
/// готовый <see cref="RecordChannel"/>. Сообщения идут с 2-байтным префиксом длины.
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

    /// <summary>
    /// Пытается принять клиента. Байты читаются с буферизацией: если это не наш
    /// клиент, всё прочитанное возвращается в <see cref="AcceptOutcome.Buffered"/>
    /// для передачи декой-прокси. Мы отвечаем (msg2) только после того, как msg1
    /// успешно аутентифицирован - зонд ответа не увидит.
    /// </summary>
    public static async Task<AcceptOutcome> TryAcceptAsync(
        Stream stream, KeyPair serverStatic, uint carrierId,
        CancellationToken cancellationToken = default)
    {
        var buffered = new List<byte>(NoiseIkHandshake.Message1Length + 2);

        // Префикс длины. У настоящего клиента он равен длине msg1; иначе это чужак.
        byte[] prefix = new byte[2];
        if (!await ReadRecordingAsync(stream, prefix, buffered, cancellationToken).ConfigureAwait(false))
            return AcceptOutcome.Cover(buffered.ToArray());

        int length = BinaryPrimitives.ReadUInt16BigEndian(prefix);
        if (length != NoiseIkHandshake.Message1Length)
            return AcceptOutcome.Cover(buffered.ToArray());

        byte[] message1 = new byte[length];
        if (!await ReadRecordingAsync(stream, message1, buffered, cancellationToken).ConfigureAwait(false))
            return AcceptOutcome.Cover(buffered.ToArray());

        var handshake = NoiseIkHandshake.CreateResponder(serverStatic);
        HandshakeResult result;
        try
        {
            handshake.ReadMessage1(message1);
        }
        catch (ChameleonProtocolException)
        {
            return AcceptOutcome.Cover([.. buffered]);
        }

        // msg1 подлинный - отвечаем и поднимаем канал.
        result = handshake.WriteMessage2(out byte[] message2);
        await WriteFrameAsync(stream, message2, cancellationToken).ConfigureAwait(false);

        var keys = KeySchedule.ForCarrier(result.SessionSecret, carrierId, isClient: false);
        return AcceptOutcome.Success(new RecordChannel(stream, keys), result.RemoteStaticPublic);
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

    /// <summary>Читает ровно buffer.Length байт, дописывая их в recorder. false - если поток кончился.</summary>
    private static async Task<bool> ReadRecordingAsync(
        Stream stream, byte[] buffer, List<byte> recorder, CancellationToken cancellationToken)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken).ConfigureAwait(false);
            if (n == 0) return false;
            read += n;
        }

        recorder.AddRange(buffer);
        return true;
    }
}