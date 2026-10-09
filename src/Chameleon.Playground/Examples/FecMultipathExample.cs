using System.Security.Cryptography;
using Chameleon.Core.Session;
using Chameleon.Core.Transport;

namespace Chameleon.Playground.Examples;

/// <summary>
/// FEC в живом цикле на мультипути: две несущие, на одной теряем 20% пакетов
/// (клиент→сервер). Сравниваем FEC ON и OFF: с FEC часть потерь закрывается
/// восстановлением (без ожидания ретрансмита), данные целы в обоих случаях.
/// </summary>
public static class FecMultipathExample
{
    public static async Task RunAsync()
    {
        Console.WriteLine("FEC на мультипути: 2 несущие, несущая A теряет 20% пакетов клиент→сервер.");
        Console.WriteLine("Данные должны дойти целыми в ОБОИХ режимах (источник истины - ACK/ретрансмит),");
        Console.WriteLine("но при FEC ON часть потерь закрывается восстановлением.\n");

        await RunAsync(fecOn: true);
        await RunAsync(fecOn: false);
    }

    private static async Task RunAsync(bool fecOn)
    {
        var (cA, sA) = DuplexStream.CreatePair();
        var (cB, sB) = DuplexStream.CreatePair();

        var fec = fecOn ? FecOptions.Default : FecOptions.Off;

        await using var server = ChameleonSession.Start(Ch(sA), isClient: false, fec: fec);
        server.AddCarrier(Ch(sB));

        var received = new MemoryStream();
        var done = new TaskCompletionSource();
        server.StreamAccepted += st => _ = Task.Run(async () =>
        {
            while (true)
            {
                var r = await st.Input.ReadAsync();
                foreach (var seg in r.Buffer) received.Write(seg.Span);
                st.Input.AdvanceTo(r.Buffer.End);
                if (r.IsCompleted) break;
            }
            done.TrySetResult();
        });
        
        var lossyA = new LossyChannel(Ch(cA), dropProb: 0.20, seed: 1);
        await using var client = ChameleonSession.Start(lossyA, isClient: true, fec: fec);
        client.AddCarrier(Ch(cB));

        byte[] payload = RandomNumberGenerator.GetBytes(300_000);
        string expected = Convert.ToHexString(SHA256.HashData(payload));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var stream = await client.OpenStreamAsync("example.com", 443);
        int off = 0;
        while (off < payload.Length)
        {
            int n = Math.Min(8000, payload.Length - off);
            await stream.WriteAsync(payload.AsMemory(off, n));
            off += n;
        }
        await stream.CompleteOutputAsync();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(30));
        sw.Stop();

        string actual = Convert.ToHexString(SHA256.HashData(received.ToArray()));
        bool ok = received.Length == payload.Length && actual == expected;

        Console.WriteLine(
            $"[FEC {(fecOn ? "ON " : "OFF")}] данные={(ok ? "целы ✓" : "БИТЫЕ ✗")}, " +
            $"потеряно на A={lossyA.Dropped} пакетов, " +
            $"восстановлено FEC={server.FecRecovered}, " +
            $"отправлено клиентом={client.BytesSent / 1024} КБ, " +
            $"время={sw.ElapsedMilliseconds} мс");

        static FrameChannel Ch(Stream s) => new(s);
    }
}

/// <summary>Несущая-обёртка, «теряющая» заданную долю исходящих пакетов (эмуляция потерь на пути).</summary>
internal sealed class LossyChannel : ICarrierChannel
{
    private readonly ICarrierChannel _inner;
    private readonly double _drop;
    private readonly Random _rng;
    private int _dropped;

    public LossyChannel(ICarrierChannel inner, double dropProb, int seed)
    {
        _inner = inner; _drop = dropProb; _rng = new Random(seed);
    }

    public int Dropped => Volatile.Read(ref _dropped);

    public ValueTask<int> ReadRecordAsync(Memory<byte> destination, CancellationToken cancellationToken = default)
        => _inner.ReadRecordAsync(destination, cancellationToken);

    public ValueTask WriteRecordAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken = default)
    {
        bool drop;
        lock (_rng) drop = _rng.NextDouble() < _drop;
        if (drop) { Interlocked.Increment(ref _dropped); return ValueTask.CompletedTask; }  // пакет «потерян»
        return _inner.WriteRecordAsync(packet, cancellationToken);
    }

    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}
