using System.Buffers;

namespace Chameleon.Tls;

/// <summary>
/// Полнодуплексная обёртка над блокирующим синхронным потоком BouncyCastle TLS.
/// Базовый Stream сериализует ReadAsync/WriteAsync общим семафором, а BC-поток
/// переопределяет только синхронные Read/Write - поэтому одновременные чтение и
/// запись через базовую реализацию встают в тупик. Здесь чтение и запись
/// выполняются независимо (на пуле потоков), записи сериализуются своим замком.
/// </summary>
public sealed class BcDuplexStream(Stream inner) : Stream
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => await Task.Run(() =>
        {
            byte[] tmp = ArrayPool<byte>.Shared.Rent(buffer.Length);
            try
            {
                int n = inner.Read(tmp, 0, buffer.Length);
                tmp.AsSpan(0, n).CopyTo(buffer.Span);
                return n;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(tmp);
            }
        }, cancellationToken).ConfigureAwait(false);

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                byte[] tmp = buffer.ToArray();
                inner.Write(tmp, 0, tmp.Length);
                inner.Flush();
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public override void Flush() => inner.Flush();
    public override int Read(byte[] b, int o, int c) => inner.Read(b, o, c);

    public override void Write(byte[] b, int o, int c)
    {
        inner.Write(b, o, c);
        inner.Flush();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
            _writeLock.Dispose();
        }
    }

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
    public override void SetLength(long v) => throw new NotSupportedException();
}