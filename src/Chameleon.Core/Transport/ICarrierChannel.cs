namespace Chameleon.Core.Transport;

/// <summary>
/// Одна несущая: превращает надёжный упорядоченный Stream (TLS/TCP) в канал
/// сессионных пакетов. Сессия работает через этот интерфейс и не знает, шифрует
/// ли канал сам (RecordChannel) или полагается на TLS снаружи (FrameChannel).
/// </summary>
public interface ICarrierChannel : IAsyncDisposable
{
    /// <summary>Читает один пакет. Возвращает его длину или -1 при корректном закрытии потока.</summary>
    ValueTask<int> ReadRecordAsync(Memory<byte> destination, CancellationToken cancellationToken = default);

    /// <summary>Отправляет один пакет.</summary>
    ValueTask WriteRecordAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken = default);
}