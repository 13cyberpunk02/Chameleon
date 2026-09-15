using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Chameleon.Core.Proxy;

/// <summary>
/// Минимальный SOCKS5 (RFC 1928), только метод «без аутентификации» и команда
/// CONNECT - этого достаточно, чтобы браузер или curl направляли трафик в туннель.
/// </summary>
public static class Socks5
{
    public readonly record struct Target(string Host, int Port);

    public static async Task<Target> HandshakeAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        byte[] head = new byte[2];
        await ReadExactAsync(stream, head, cancellationToken).ConfigureAwait(false);
        if (head[0] != 0x05) throw new IOException("Не SOCKS5");
        await ReadExactAsync(stream, new byte[head[1]], cancellationToken).ConfigureAwait(false);

        await stream.WriteAsync(new byte[] { 0x05, 0x00 }, cancellationToken).ConfigureAwait(false);

        byte[] request = new byte[4];
        await ReadExactAsync(stream, request, cancellationToken).ConfigureAwait(false);
        if (request[1] != 0x01)
        {
            await ReplyAsync(stream, 0x07, cancellationToken).ConfigureAwait(false);
            throw new IOException("Поддерживается только CONNECT");
        }

        string host = request[3] switch
        {
            0x01 => await ReadIPv4Async(stream, cancellationToken).ConfigureAwait(false),
            0x03 => await ReadDomainAsync(stream, cancellationToken).ConfigureAwait(false),
            0x04 => await ReadIPv6Async(stream, cancellationToken).ConfigureAwait(false),
            _ => throw new IOException("Неизвестный тип адреса"),
        };

        byte[] portBytes = new byte[2];
        await ReadExactAsync(stream, portBytes, cancellationToken).ConfigureAwait(false);
        int port = BinaryPrimitives.ReadUInt16BigEndian(portBytes);

        await ReplyAsync(stream, 0x00, cancellationToken).ConfigureAwait(false);
        return new Target(host, port);
    }

    private static async Task<string> ReadIPv4Async(NetworkStream stream, CancellationToken cancellationToken)
    {
        byte[] address = new byte[4];
        await ReadExactAsync(stream, address, cancellationToken).ConfigureAwait(false);
        return new IPAddress(address).ToString();
    }

    private static async Task<string> ReadIPv6Async(NetworkStream stream, CancellationToken cancellationToken)
    {
        byte[] address = new byte[16];
        await ReadExactAsync(stream, address, cancellationToken).ConfigureAwait(false);
        return new IPAddress(address).ToString();
    }

    private static async Task<string> ReadDomainAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        byte[] lengthByte = new byte[1];
        await ReadExactAsync(stream, lengthByte, cancellationToken).ConfigureAwait(false);
        byte[] domain = new byte[lengthByte[0]];
        await ReadExactAsync(stream, domain, cancellationToken).ConfigureAwait(false);
        return Encoding.ASCII.GetString(domain);
    }

    private static Task ReplyAsync(NetworkStream stream, byte code, CancellationToken cancellationToken)
    {
        byte[] reply = [0x05, code, 0x00, 0x01, 0, 0, 0, 0, 0, 0];
        return stream.WriteAsync(reply, cancellationToken).AsTask();
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