namespace Chameleon.Core.Protocol;

public enum FrameType : byte
{
    Padding = 0x00,
    Ping = 0x01,
    Ack = 0x02,
    StreamOpen = 0x03,
    Stream = 0x04,
    StreamFin = 0x05,
    StreamReset = 0x06,
    MaxData = 0x07,
    Close = 0x08,
}

public enum StreamKind : byte
{
    Tcp = 0,
    Udp = 1
}

public enum AddressType : byte
{
    IPv4 = 1,
    IPv6 = 2,
    Domain = 3
}

/// <summary>Диапазон в ACK: сколько пакетов пропущено (Gap) и сколько подтверждено (Length).</summary>
public readonly record struct AckRange(ulong Gap, ulong Length);

public abstract record Frame(FrameType Type);

public sealed record PingFrame() : Frame(FrameType.Ping);

/// <summary>
/// Подтверждает пакеты: LargestAcked и FirstRange предыдущих подряд,
/// затем пары (пропуск, подтверждено), идущие вниз по номерам.
/// </summary>
public sealed record AckFrame(ulong LargestAcked, ulong AckDelayMs, ulong FirstRange, IReadOnlyList<AckRange> Ranges)
    : Frame(FrameType.Ack);

public sealed record StreamOpenFrame(ulong StreamId, StreamKind Kind, AddressType AddressType, string Host, ushort Port)
    : Frame(FrameType.StreamOpen);

/// <summary>
/// Data ссылается на буфер, из которого разобран пакет (без копирования).
/// Если буфер будет переиспользован, данные нужно скопировать заранее.
/// </summary>
public sealed record StreamFrame(ulong StreamId, ulong Offset, ReadOnlyMemory<byte> Data)
    : Frame(FrameType.Stream);

public sealed record StreamFinFrame(ulong StreamId, ulong FinalOffset) : Frame(FrameType.StreamFin);

public sealed record StreamResetFrame(ulong StreamId, ulong ErrorCode) : Frame(FrameType.StreamReset);

/// <summary>Управление потоком. StreamId = 0 означает лимит на всю сессию.</summary>
public sealed record MaxDataFrame(ulong StreamId, ulong MaxOffset) : Frame(FrameType.MaxData);

public sealed record CloseFrame(ulong ErrorCode, string Reason) : Frame(FrameType.Close);