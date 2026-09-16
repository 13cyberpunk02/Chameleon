using System.Buffers.Binary;

namespace Chameleon.Core.Fec;

/// <summary>
/// Оборачивает <see cref="ReedSolomon"/> для пакетов переменной длины. Блок из k
/// пакетов кодируется в k+m равных шардов; потеряв до m любых шардов, приёмник
/// восстанавливает все исходные пакеты. Каждый пакет предваряется 2-байтной
/// длиной и дополняется до размера шарда.
///
/// В мультипути repair-шарды раскладываются по разным несущим: троттлинг или
/// потеря одной несущей компенсируется восстановлением, а не ожиданием
/// ретрансмита (лишний RTT).
/// </summary>
public sealed class FecBlock
{
    private readonly ReedSolomon _rs;

    public FecBlock(int dataShards, int parityShards) => _rs = new ReedSolomon(dataShards, parityShards);

    public int DataShards => _rs.DataShards;
    public int ParityShards => _rs.ParityShards;
    public int TotalShards => _rs.TotalShards;

    /// <summary>Кодирует блок-пакетов в k+m шардов равной длины.</summary>
    public byte[][] Encode(IReadOnlyList<byte[]> packets)
    {
        if (packets.Count != DataShards)
            throw new ArgumentException($"Ожидалось ровно {DataShards} пакетов в блоке");

        int shardSize = 2 + packets.Max(p => p.Length);
        var shards = new byte[TotalShards][];
        for (int i = 0; i < DataShards; i++)
        {
            var shard = new byte[shardSize];
            BinaryPrimitives.WriteUInt16BigEndian(shard, (ushort)packets[i].Length);
            packets[i].CopyTo(shard.AsSpan(2));
            shards[i] = shard;
        }

        byte[][] parity = _rs.Encode(shards[..DataShards]);
        for (int i = 0; i < ParityShards; i++) shards[DataShards + i] = parity[i];
        return shards;
    }

    /// <summary>
    /// Восстанавливает исходные пакеты из уцелевших шардов. present[i]=false -
    /// шард потерян (может быть нулевым массивом нужной длины).
    /// </summary>
    public byte[][] Decode(byte[][] shards, bool[] present)
    {
        _rs.DecodeMissingData(shards, present);

        var packets = new byte[DataShards][];
        for (int i = 0; i < DataShards; i++)
        {
            int len = BinaryPrimitives.ReadUInt16BigEndian(shards[i]);
            packets[i] = shards[i].AsSpan(2, len).ToArray();
        }

        return packets;
    }
}