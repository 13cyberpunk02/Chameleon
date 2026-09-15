using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace Chameleon.Core.Protocol;

/// <summary>
/// Разбирает расшифрованный сессионный пакет. Входные данные считаются враждебными:
/// любые выходы за границы и неизвестные типы дают <see cref="ChameleonProtocolException"/>.
/// </summary>
public static class PacketReader
{
    private const int MaxAckRanges = 256;

    /// <returns>Номер пакета. Фреймы добавляются в <paramref name="frames"/>.</returns>
    public static ulong Parse(ReadOnlyMemory<byte> packet, List<Frame> frames)
    {
        var span = packet.Span;
        int pos = 0;
        ulong packetNumber = ReadVarInt(span, ref pos);

        while (pos < span.Length)
        {
            var type = (FrameType)span[pos++];
            switch (type)
            {
                case FrameType.Padding:
                    break;

                case FrameType.Ping:
                    frames.Add(new PingFrame());
                    break;

                case FrameType.Ack:
                {
                    ulong largest = ReadVarInt(span, ref pos);
                    ulong delay = ReadVarInt(span, ref pos);
                    ulong count = ReadVarInt(span, ref pos);
                    ulong first = ReadVarInt(span, ref pos);
                    if (count > MaxAckRanges) throw Error("Слишком много диапазонов в ACK");

                    var ranges = new AckRange[(int)count];
                    for (int i = 0; i < ranges.Length; i++)
                        ranges[i] = new AckRange(ReadVarInt(span, ref pos), ReadVarInt(span, ref pos));

                    frames.Add(new AckFrame(largest, delay, first, ranges));
                    break;
                }

                case FrameType.StreamOpen:
                {
                    ulong streamId = ReadVarInt(span, ref pos);
                    var kind = (StreamKind)ReadByte(span, ref pos);
                    if (kind is not (StreamKind.Tcp or StreamKind.Udp)) throw Error("Неизвестный тип потока");

                    var addressType = (AddressType)ReadByte(span, ref pos);
                    string host = addressType switch
                    {
                        AddressType.IPv4 => new IPAddress(ReadBytes(span, ref pos, 4)).ToString(),
                        AddressType.IPv6 => new IPAddress(ReadBytes(span, ref pos, 16)).ToString(),
                        AddressType.Domain =>
                            Encoding.UTF8.GetString(ReadBytes(span, ref pos, ReadByte(span, ref pos))),
                        _ => throw Error("Неизвестный тип адреса"),
                    };
                    ushort port = BinaryPrimitives.ReadUInt16BigEndian(ReadBytes(span, ref pos, 2));

                    frames.Add(new StreamOpenFrame(streamId, kind, addressType, host, port));
                    break;
                }

                case FrameType.Stream:
                {
                    ulong streamId = ReadVarInt(span, ref pos);
                    ulong offset = ReadVarInt(span, ref pos);
                    int length = CheckedLength(ReadVarInt(span, ref pos), span.Length - pos);
                    frames.Add(new StreamFrame(streamId, offset, packet.Slice(pos, length)));
                    pos += length;
                    break;
                }

                case FrameType.StreamFin:
                    frames.Add(new StreamFinFrame(ReadVarInt(span, ref pos), ReadVarInt(span, ref pos)));
                    break;

                case FrameType.StreamReset:
                    frames.Add(new StreamResetFrame(ReadVarInt(span, ref pos), ReadVarInt(span, ref pos)));
                    break;

                case FrameType.MaxData:
                    frames.Add(new MaxDataFrame(ReadVarInt(span, ref pos), ReadVarInt(span, ref pos)));
                    break;

                case FrameType.Close:
                {
                    ulong code = ReadVarInt(span, ref pos);
                    ulong rawLength = ReadVarInt(span, ref pos);
                    if (rawLength > PacketWriter.MaxCloseReasonBytes) throw Error("Слишком длинная причина закрытия");
                    int length = CheckedLength(rawLength, span.Length - pos);
                    frames.Add(new CloseFrame(code, Encoding.UTF8.GetString(span.Slice(pos, length))));
                    pos += length;
                    break;
                }

                default:
                    throw Error($"Неизвестный тип фрейма 0x{(byte)type:X2}");
            }
        }

        return packetNumber;
    }

    private static ulong ReadVarInt(ReadOnlySpan<byte> span, ref int pos)
    {
        if (!VarInt.TryRead(span[pos..], out ulong value, out int consumed))
            throw Error("Обрезанный varint");
        pos += consumed;
        return value;
    }

    private static byte ReadByte(ReadOnlySpan<byte> span, ref int pos)
    {
        if (pos >= span.Length) throw Error("Неожиданный конец пакета");
        return span[pos++];
    }

    private static ReadOnlySpan<byte> ReadBytes(ReadOnlySpan<byte> span, ref int pos, int count)
    {
        if (count > span.Length - pos) throw Error("Неожиданный конец пакета");
        var result = span.Slice(pos, count);
        pos += count;
        return result;
    }

    private static int CheckedLength(ulong length, int available)
    {
        if (length > (ulong)available) throw Error("Длина фрейма выходит за пакет");
        return (int)length;
    }

    private static ChameleonProtocolException Error(string message) => new(message);
}