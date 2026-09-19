using System.Buffers.Binary;
using Chameleon.Core.Crypto;

namespace Chameleon.Core.Transport;

/// <summary>
/// Итог попытки принять входящую несущую как НОВУЮ сессию (полное рукопожатие
/// Noise). Либо аутентифицированный клиент (<see cref="Channel"/> задан) с
/// секретом сессии, либо чужак/зонд - тогда <see cref="Buffered"/> содержит уже
/// прочитанные байты для переигрывания декой-прокси.
/// </summary>
public sealed class AcceptOutcome
{
    private AcceptOutcome(ICarrierChannel? channel, byte[]? clientStaticPublic, byte[]? sessionSecret, byte[] buffered)
    {
        Channel = channel;
        ClientStaticPublic = clientStaticPublic;
        SessionSecret = sessionSecret;
        Buffered = buffered;
    }

    public ICarrierChannel? Channel { get; }
    public byte[]? ClientStaticPublic { get; }
    public byte[]? SessionSecret { get; }
    public byte[] Buffered { get; }
    public bool Succeeded => Channel is not null;

    internal static AcceptOutcome Success(ICarrierChannel channel, byte[] clientStaticPublic, byte[] sessionSecret)
        => new(channel, clientStaticPublic, sessionSecret, []);

    internal static AcceptOutcome Cover(byte[] buffered) => new(null, null, null, buffered);
}

/// <summary>Рукопожатие Noise IK поверх Stream (внутри TLS-несущей).</summary>
public static class ChameleonHandshake
{
    /// <returns>Канал несущей и секрет сессии (нужен для присоединения других несущих).</returns>
    public static async Task<(ICarrierChannel Channel, byte[] SessionSecret)> ConnectAsync(
        Stream stream, KeyPair clientStatic, byte[] serverStaticPublic, uint carrierId,
        CancellationToken cancellationToken = default)
    {
        var handshake = NoiseIkHandshake.CreateInitiator(clientStatic, serverStaticPublic);

        await Framing.WriteFrameAsync(stream, handshake.WriteMessage1(), cancellationToken).ConfigureAwait(false);
        byte[] prefix = new byte[2];
        int len = await Framing.ReadPrefixAsync(stream, prefix, cancellationToken).ConfigureAwait(false);
        byte[] message2 = await Framing.ReadExactCountAsync(stream, len, cancellationToken).ConfigureAwait(false);
        HandshakeResult result = handshake.ReadMessage2(message2);
        
        return (new FrameChannel(stream), result.SessionSecret);
    }

    /// <summary>
    /// Принимает входящую несущую как новую сессию. Байты буферизуются: если это
    /// не наш клиент, всё прочитанное уходит в Buffered для декой-прокси.
    /// </summary>
    public static async Task<AcceptOutcome> TryAcceptAsync(
        Stream stream, KeyPair serverStatic, uint carrierId, CancellationToken cancellationToken = default)
    {
        var buffered = new List<byte>(NoiseIkHandshake.Message1Length + 2);

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
        try
        {
            handshake.ReadMessage1(message1);
        }
        catch (ChameleonProtocolException)
        {
            return AcceptOutcome.Cover(buffered.ToArray());
        }

        HandshakeResult result = handshake.WriteMessage2(out byte[] message2);
        await Framing.WriteFrameAsync(stream, message2, cancellationToken).ConfigureAwait(false);

        return AcceptOutcome.Success(new FrameChannel(stream), result.RemoteStaticPublic, result.SessionSecret);
    }

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