using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Chameleon.Core.Proxy;

/// <summary>
/// Минимальный SOCKS5 (RFC 1928): методы без аутентификации, команды CONNECT (TCP)
/// и UDP ASSOCIATE (UDP). Достаточно для браузера, curl и tun2socks.
/// </summary>
public static class Socks5
{
    public enum Command : byte
    {
        Connect = 1,
        UdpAssociate = 3
    }

    public readonly record struct Request(Command Command, string Host, int Port);

    /// <summary>Читает приветствие и запрос (без отправки ответа - ответ шлёт вызывающий).</summary>
    public static async Task<Request> ReadRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        byte[] head = new byte[2];
        await ReadExactAsync(stream, head, cancellationToken).ConfigureAwait(false);
        if (head[0] != 0x05) throw new IOException("Не SOCKS5");
        await ReadExactAsync(stream, new byte[head[1]], cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(new byte[] { 0x05, 0x00 }, cancellationToken)
            .ConfigureAwait(false);

        byte[] request = new byte[4];
        await ReadExactAsync(stream, request, cancellationToken).ConfigureAwait(false);
        var command = (Command)request[1];

        string host = request[3] switch
        {
            0x01 => new IPAddress(await ReadN(stream, 4, cancellationToken)).ToString(),
            0x03 => Encoding.ASCII.GetString(await ReadN(stream, (await ReadN(stream, 1, cancellationToken))[0],
                cancellationToken)),
            0x04 => new IPAddress(await ReadN(stream, 16, cancellationToken)).ToString(),
            _ => throw new IOException("Неизвестный тип адреса"),
        };
        byte[] portBytes = await ReadN(stream, 2, cancellationToken);
        int port = BinaryPrimitives.ReadUInt16BigEndian(portBytes);
        return new Request(command, host, port);
    }

    /// <summary>Ответ клиенту: код + привязанный адрес (для UDP - адрес UDP-релея).</summary>
    public static Task ReplyAsync(NetworkStream stream, byte code, IPEndPoint bound,
        CancellationToken cancellationToken)
    {
        byte[] addr = bound.Address.GetAddressBytes();
        byte atyp = (byte)(addr.Length == 4 ? 0x01 : 0x04);
        byte[] reply = new byte[4 + addr.Length + 2];
        reply[0] = 0x05;
        reply[1] = code;
        reply[2] = 0x00;
        reply[3] = atyp;
        addr.CopyTo(reply, 4);
        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(4 + addr.Length), (ushort)bound.Port);
        return stream.WriteAsync(reply, cancellationToken).AsTask();
    }

    private static async Task<byte[]> ReadN(NetworkStream s, int n, CancellationToken ct)
    {
        byte[] b = new byte[n];
        await ReadExactAsync(s, b, ct).ConfigureAwait(false);
        return b;
    }

    private static async Task ReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken).ConfigureAwait(false);
            if (n == 0) throw new IOException("Соединение закрыто во время SOCKS5-рукопожатия");
            read += n;
        }
    }
}