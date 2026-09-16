using System.Buffers;
using System.IO.Pipelines;
using Chameleon.Core.Protocol;

namespace Chameleon.Core.Session;

/// <summary>
/// Один логический поток внутри сессии. Данные ОТ собеседника приходят по offset
/// (возможно, вразнобой с разных несущих и с дублями от ретрансмитов), поэтому
/// здесь они пересобираются: дубли отбрасываются, «дырки» ждут, а наружу в
/// <see cref="Input"/> отдаётся строго непрерывный упорядоченный поток.
/// </summary>
public sealed class ChameleonStream
{
    private readonly ChameleonSession _session;

    private readonly Pipe _inbound = new(new PipeOptions(
        pauseWriterThreshold: 1 << 20, resumeWriterThreshold: 1 << 19, useSynchronizationContext: false));

    private readonly SemaphoreSlim _reasmLock = new(1, 1);
    private readonly SortedDictionary<ulong, byte[]> _segments = new();
    private ulong _deliveredOffset;
    private ulong? _finalOffset;
    private int _inboundCompleted;

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
    public string? TargetHost { get; }
    public int TargetPort { get; }
    public PipeReader Input => _inbound.Reader;

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        ulong offset = _sendOffset;
        _sendOffset += (ulong)data.Length;
        await _session.SendStreamDataAsync(Id, offset, data, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask CompleteOutputAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _outputCompleted, 1) == 0)
            await _session.SendStreamFinAsync(Id, _sendOffset, cancellationToken).ConfigureAwait(false);
    }

    // --- приём с пересборкой по offset (вызывается циклами приёма несущих) ---

    internal async ValueTask ReceiveDataAsync(ulong offset, ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken)
    {
        await _reasmLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Отрезаем уже доставленное (дубль/перекрытие от ретрансмита).
            if (offset < _deliveredOffset)
            {
                ulong skip = _deliveredOffset - offset;
                if (skip >= (ulong)data.Length) return; // полностью дубль
                data = data[(int)skip..];
                offset = _deliveredOffset;
            }

            if (offset > _deliveredOffset)
            {
                _segments.TryAdd(offset, data.ToArray()); // «дырка» - в буфер
            }
            else
            {
                await WriteAndAdvanceAsync(data, cancellationToken).ConfigureAwait(false);
                await DrainAsync(cancellationToken).ConfigureAwait(false);
            }

            await MaybeCompleteAsync().ConfigureAwait(false);
        }
        finally
        {
            _reasmLock.Release();
        }
    }

    internal async ValueTask ReceiveFinAsync(ulong finalOffset, CancellationToken cancellationToken)
    {
        await _reasmLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _finalOffset = _finalOffset is { } f ? Math.Min(f, finalOffset) : finalOffset;
            await DrainAsync(cancellationToken).ConfigureAwait(false);
            await MaybeCompleteAsync().ConfigureAwait(false);
        }
        finally
        {
            _reasmLock.Release();
        }
    }

    private async ValueTask WriteAndAdvanceAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        _inbound.Writer.Write(data.Span);
        _deliveredOffset += (ulong)data.Length;
        FlushResult r = await _inbound.Writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (r.IsCompleted) CompleteInbound();
    }

    private async ValueTask DrainAsync(CancellationToken cancellationToken)
    {
        while (_segments.Count > 0)
        {
            var first = _segments.First();
            if (first.Key > _deliveredOffset) break; // всё ещё дырка

            _segments.Remove(first.Key);
            ReadOnlyMemory<byte> seg = first.Value;
            if (first.Key < _deliveredOffset)
            {
                ulong skip = _deliveredOffset - first.Key;
                if (skip >= (ulong)seg.Length) continue; // дубль
                seg = seg[(int)skip..];
            }

            await WriteAndAdvanceAsync(seg, cancellationToken).ConfigureAwait(false);
        }
    }

    private ValueTask MaybeCompleteAsync()
    {
        if (_finalOffset is { } f && _deliveredOffset >= f)
            CompleteInbound();
        return ValueTask.CompletedTask;
    }

    internal void CompleteInbound(Exception? error = null)
    {
        if (Interlocked.Exchange(ref _inboundCompleted, 1) == 0)
            _inbound.Writer.Complete(error);
    }
}