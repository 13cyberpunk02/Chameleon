using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Net;
using System.Text;
using Chameleon.Core.Session;

namespace Chameleon.Core.Proxy;

/// <summary>Адрес назначения датаграммы (IPv4/IPv6/домен) + порт.</summary>
public readonly record struct UdpTarget(byte AddressType, byte[] Address, ushort Port)
{
    public const byte IPv4 = 1, Domain = 3, IPv6 = 4;

    public static UdpTarget FromEndPoint(IPEndPoint ep)
    {
        byte[] addr = ep.Address.GetAddressBytes();
        return new UdpTarget(addr.Length == 4 ? IPv4 : IPv6, addr, (ushort)ep.Port);
    }

    public static UdpTarget FromDomain(string host, ushort port) => new(Domain, Encoding.ASCII.GetBytes(host), port);

    /// <summary>Резолвит в IPEndPoint (для домена - через DNS сервера).</summary>
    public async ValueTask<IPEndPoint> ResolveAsync(CancellationToken ct)
    {
        if (AddressType == Domain)
        {
            var addrs = await Dns.GetHostAddressesAsync(Encoding.ASCII.GetString(Address), ct).ConfigureAwait(false);
            return new IPEndPoint(addrs[0], Port);
        }

        return new IPEndPoint(new IPAddress(Address), Port);
    }

    public string HostString =>
        AddressType == Domain ? Encoding.ASCII.GetString(Address) : new IPAddress(Address).ToString();
}

/// <summary>
/// Кадрирование датаграмм поверх надёжного потока сессии. Одна датаграмма:
///   [len: uint16 BE] [atyp:1] [addr: 4|16| (1+n)] [port: uint16 BE] [payload]
/// len покрывает всё после себя. Надёжный поток гарантирует границы сообщений.
/// </summary>
public static class UdpDatagram
{
    public static async ValueTask WriteAsync(ChameleonStream stream, UdpTarget target, ReadOnlyMemory<byte> payload,
        CancellationToken ct)
    {
        int addrLen = target.AddressType == UdpTarget.Domain ? 1 + target.Address.Length : target.Address.Length;
        int body = 1 + addrLen + 2 + payload.Length;
        byte[] buf = ArrayPool<byte>.Shared.Rent(2 + body);
        try
        {
            int p = 0;
            BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(p), (ushort)body);
            p += 2;
            buf[p++] = target.AddressType;
            if (target.AddressType == UdpTarget.Domain) buf[p++] = (byte)target.Address.Length;
            target.Address.CopyTo(buf.AsSpan(p));
            p += target.Address.Length;
            BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(p), target.Port);
            p += 2;
            payload.Span.CopyTo(buf.AsSpan(p));
            p += payload.Length;
            await stream.WriteAsync(buf.AsMemory(0, p), ct).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    /// <summary>Читает по одной датаграмме из потока, вызывая обработчик. Завершается при закрытии потока.</summary>
    public static async Task ReadLoopAsync(PipeReader reader,
        Func<UdpTarget, byte[], CancellationToken, ValueTask> onDatagram, CancellationToken ct)
    {
        while (true)
        {
            ReadResult r = await reader.ReadAsync(ct).ConfigureAwait(false);
            ReadOnlySequence<byte> buffer = r.Buffer;
            while (TryParse(ref buffer, out UdpTarget target, out byte[] payload))
                await onDatagram(target, payload, ct).ConfigureAwait(false);
            reader.AdvanceTo(buffer.Start, buffer.End);
            if (r.IsCompleted) break;
        }

        await reader.CompleteAsync().ConfigureAwait(false);
    }

    private static bool TryParse(ref ReadOnlySequence<byte> buffer, out UdpTarget target, out byte[] payload)
    {
        target = default;
        payload = [];
        if (buffer.Length < 2) return false;
        Span<byte> lenb = stackalloc byte[2];
        buffer.Slice(0, 2).CopyTo(lenb);
        int body = BinaryPrimitives.ReadUInt16BigEndian(lenb);
        if (buffer.Length < 2 + body) return false;

        byte[] block = buffer.Slice(2, body).ToArray();
        int p = 0;
        byte atyp = block[p++];
        byte[] addr;
        if (atyp == UdpTarget.Domain)
        {
            int n = block[p++];
            addr = block[p..(p + n)];
            p += n;
        }
        else
        {
            int n = atyp == UdpTarget.IPv4 ? 4 : 16;
            addr = block[p..(p + n)];
            p += n;
        }

        ushort port = BinaryPrimitives.ReadUInt16BigEndian(block.AsSpan(p));
        p += 2;
        payload = block[p..];
        target = new UdpTarget(atyp, addr, port);
        buffer = buffer.Slice(2 + body);
        return true;
    }
}