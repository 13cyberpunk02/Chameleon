using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace Chameleon.Core.Protocol;

/// <summary>
/// Собирает сессионный пакет прямо в переданный буфер, без аллокаций.
/// Перед записью крупных фреймов проверяй <see cref="Remaining"/>:
/// при нехватке места бросается исключение и пакет считается испорченным.
/// </summary>
public ref struct PacketWriter
{
    public const int MaxCloseReasonBytes = 1024;

    private readonly Span<byte> _buffer;
    private int _position;

    public PacketWriter(Span<byte> buffer, ulong packetNumber)
    {
        _buffer = buffer;
        _position = 0;
        WriteVarInt(packetNumber);
    }

    public readonly int Length => _position;
    public readonly int Remaining => _buffer.Length - _position;

    public void WritePing() => WriteByte((byte)FrameType.Ping);

    public void WriteAck(ulong largestAcked, ulong ackDelayMs, ulong firstRange, scoped ReadOnlySpan<AckRange> ranges)
    {
        WriteByte((byte)FrameType.Ack);
        WriteVarInt(largestAcked);
        WriteVarInt(ackDelayMs);
        WriteVarInt((ulong)ranges.Length);
        WriteVarInt(firstRange);
        foreach (var range in ranges)
        {
            WriteVarInt(range.Gap);
            WriteVarInt(range.Length);
        }
    }

    public void WriteStreamOpen(ulong streamId, StreamKind kind, string host, ushort port)
    {
        WriteByte((byte)FrameType.StreamOpen);
        WriteVarInt(streamId);
        WriteByte((byte)kind);

        if (IPAddress.TryParse(host, out var ip))
        {
            Span<byte> raw = stackalloc byte[16];
            if (!ip.TryWriteBytes(raw, out int written))
                throw new ArgumentException("Не удалось сериализовать IP", nameof(host));
            WriteByte((byte)(written == 4 ? AddressType.IPv4 : AddressType.IPv6));
            WriteBytes(raw[..written]);
        }
        else
        {
            int byteCount = Encoding.UTF8.GetByteCount(host);
            if (byteCount is 0 or > 255)
                throw new ArgumentException("Домен должен быть от 1 до 255 байт", nameof(host));
            WriteByte((byte)AddressType.Domain);
            WriteByte((byte)byteCount);
            EnsureSpace(byteCount);
            _position += Encoding.UTF8.GetBytes(host, _buffer[_position..]);
        }

        Span<byte> portBytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(portBytes, port);
        WriteBytes(portBytes);
    }

    /// <summary>
    /// Пишет столько данных, сколько влезает. Возвращает число записанных байт данных
    /// (0, если не влез даже заголовок фрейма). Остаток отправляется следующим пакетом.
    /// </summary>
    public int WriteStream(ulong streamId, ulong offset, scoped ReadOnlySpan<byte> data)
    {
        int header = 1 + VarInt.SizeOf(streamId) + VarInt.SizeOf(offset) + VarInt.SizeOf((ulong)data.Length);
        int count = Math.Min(data.Length, Remaining - header);
        if (count <= 0) return 0;

        WriteByte((byte)FrameType.Stream);
        WriteVarInt(streamId);
        WriteVarInt(offset);
        WriteVarInt((ulong)count);
        WriteBytes(data[..count]);
        return count;
    }

    public void WriteStreamFin(ulong streamId, ulong finalOffset)
    {
        WriteByte((byte)FrameType.StreamFin);
        WriteVarInt(streamId);
        WriteVarInt(finalOffset);
    }

    public void WriteStreamReset(ulong streamId, ulong errorCode)
    {
        WriteByte((byte)FrameType.StreamReset);
        WriteVarInt(streamId);
        WriteVarInt(errorCode);
    }

    public void WriteMaxData(ulong streamId, ulong maxOffset)
    {
        WriteByte((byte)FrameType.MaxData);
        WriteVarInt(streamId);
        WriteVarInt(maxOffset);
    }

    public void WriteClose(ulong errorCode, string reason)
    {
        int byteCount = Encoding.UTF8.GetByteCount(reason);
        if (byteCount > MaxCloseReasonBytes)
            throw new ArgumentException("Слишком длинная причина", nameof(reason));

        WriteByte((byte)FrameType.Close);
        WriteVarInt(errorCode);
        WriteVarInt((ulong)byteCount);
        EnsureSpace(byteCount);
        _position += Encoding.UTF8.GetBytes(reason, _buffer[_position..]);
    }

    /// <summary>Добивает пакет нулями (фреймы PADDING) до нужного размера. Главный инструмент шейпера.</summary>
    public void PadTo(int totalLength)
    {
        if (totalLength > _buffer.Length)
            throw new ArgumentOutOfRangeException(nameof(totalLength), "Больше размера буфера");
        if (totalLength <= _position) return;

        _buffer[_position..totalLength].Clear();
        _position = totalLength;
    }

    private void WriteVarInt(ulong value) => _position += VarInt.Write(_buffer[_position..], value);

    private void WriteByte(byte value)
    {
        EnsureSpace(1);
        _buffer[_position++] = value;
    }

    private void WriteBytes(scoped ReadOnlySpan<byte> bytes)
    {
        EnsureSpace(bytes.Length);
        bytes.CopyTo(_buffer[_position..]);
        _position += bytes.Length;
    }

    private readonly void EnsureSpace(int count)
    {
        if (count > _buffer.Length - _position)
            throw new InvalidOperationException("Недостаточно места в пакете");
    }
}