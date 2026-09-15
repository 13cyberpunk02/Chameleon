using System.Buffers.Binary;

namespace Chameleon.Core.Protocol;

/// <summary>
/// Целое переменной длины, как в QUIC (RFC 9000, раздел 16).
/// Два старших бита первого байта: 00 → 1 байт, 01 → 2, 10 → 4, 11 → 8.
/// </summary>
public static class VarInt
{
    public const ulong MaxValue = (1UL << 62) - 1;

    public static int SizeOf(ulong value) => value switch
    {
        < 1UL << 6 => 1,
        < 1UL << 14 => 2,
        < 1UL << 30 => 4,
        <= MaxValue => 8,
        _ => throw new ArgumentOutOfRangeException(nameof(value), "Больше 2^62-1"),
    };

    public static int Write(Span<byte> destination, ulong value)
    {
        int size = SizeOf(value);
        if (destination.Length < size)
            throw new ArgumentException("Буфер слишком мал", nameof(destination));

        switch (size)
        {
            case 1: destination[0] = (byte)value; break;
            case 2: BinaryPrimitives.WriteUInt16BigEndian(destination, (ushort)(value | 0x4000)); break;
            case 4: BinaryPrimitives.WriteUInt32BigEndian(destination, (uint)(value | 0x8000_0000)); break;
            default: BinaryPrimitives.WriteUInt64BigEndian(destination, value | 0xC000_0000_0000_0000); break;
        }

        return size;
    }

    public static bool TryRead(ReadOnlySpan<byte> source, out ulong value, out int consumed)
    {
        value = 0;
        consumed = 0;
        if (source.IsEmpty) return false;

        int size = 1 << (source[0] >> 6);
        if (source.Length < size) return false;

        value = size switch
        {
            1 => (ulong)(source[0] & 0x3F),
            2 => BinaryPrimitives.ReadUInt16BigEndian(source) & 0x3FFFUL,
            4 => BinaryPrimitives.ReadUInt32BigEndian(source) & 0x3FFF_FFFFUL,
            _ => BinaryPrimitives.ReadUInt64BigEndian(source) & 0x3FFF_FFFF_FFFF_FFFFUL,
        };
        consumed = size;
        return true;
    }
}