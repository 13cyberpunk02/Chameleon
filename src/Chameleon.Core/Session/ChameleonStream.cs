using System.Buffers;
using System.IO.Pipelines;
using Chameleon.Core.Protocol;

namespace Chameleon.Core.Session;

/// <summary>
/// Один логический поток внутри сессии (эквивалент одного TCP-соединения
/// пользователя). Данные ОТ собеседника попадают в <see cref="Input"/>,
/// данные К собеседнику отправляются через <see cref="WriteAsync"/>.
///
/// Пока несущая одна и надёжная, порядок и доставку гарантирует транспорт под
/// нами, поэтому offset здесь только проставляется, но для пересборки не нужен.
/// </summary>
public sealed class ChameleonStream
{
    private readonly ChameleonSession _session;

    private readonly Pipe _inbound = new(new PipeOptions(
        pauseWriterThreshold: 256 * 1024,
        resumeWriterThreshold: 128 * 1024,
        useSynchronizationContext: false));

    private ulong _sendOffset;
    private int _outputCompleted;

    internal ChameleonStream(ChameleonSession session, ulong id, StreamKind kind, string? targetHost, int targetPort)
    {
        _session = session;
        Id = id;
        Kind = kind;
        TargetHost = targetHost;
        TargetPort = targetPort;
    }

    public ulong Id { get; }
    public StreamKind Kind { get; }

    /// <summary>Куда подключаться (заполнено на принимающей стороне из STREAM_OPEN).</summary>
    public string? TargetHost { get; }

    public int TargetPort { get; }

    /// <summary>Поток байт, приходящих от собеседника.</summary>
    public PipeReader Input => _inbound.Reader;

    /// <summary>Отправить данные собеседнику. Крупные буферы режутся на несколько пакетов.</summary>
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        ulong offset = _sendOffset;
        _sendOffset += (ulong)data.Length;
        await _session.SendStreamDataAsync(Id, offset, data, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Сообщить, что мы больше не будем слать данные (половинное закрытие).</summary>
    public async ValueTask CompleteOutputAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _outputCompleted, 1) == 0)
            await _session.SendStreamFinAsync(Id, _sendOffset, cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask ReceiveDataAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        _inbound.Writer.Write(data.Span);
        FlushResult result = await _inbound.Writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (result.IsCompleted)
            _inbound.Writer.Complete();
    }

    internal void CompleteInbound(Exception? error = null) => _inbound.Writer.Complete(error);
}