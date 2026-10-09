using System.Buffers.Binary;
using Chameleon.Core.Protocol;

namespace Chameleon.Core.Fec;

/// <summary>
/// Накопитель FEC на стороне отправителя: копит k отправленных надёжных пакетов и,
/// как только блок заполнен, выдаёт m repair-шардов (Reed-Solomon) для рассылки по
/// несущим. Работает с Недополненным содержимым пакета (до pad-шейпера), чтобы шард
/// не раздувался паддингом. Потокобезопасен.
/// </summary>
public sealed class FecEncoder
{
    private readonly int _k;
    private readonly int _m;
    private readonly object _lock = new();
    private readonly List<(ulong Pn, byte[] Data, int Len)> _block;
    private ulong _nextBlockId;

    public FecEncoder(int dataShards, int parityShards)
    {
        if (dataShards < 1 || parityShards < 1) throw new ArgumentOutOfRangeException(nameof(dataShards));
        _k = dataShards;
        _m = parityShards;
        _block = new List<(ulong, byte[], int)>(_k);
    }

    public int DataShards => _k;
    public int ParityShards => _m;

    /// <summary>Готовый repair-шард для отправки отдельным (ненадёжным) пакетом.</summary>
    public readonly record struct Repair(
        ulong BlockId,
        int ParityCount,
        int ShardIndex,
        int ShardSize,
        FecMember[] Members,
        byte[] Parity);

    /// <summary>
    /// Зарегистрировать отправленный надёжный пакет. <paramref name="data"/> - буфер, в
    /// котором первые <paramref name="len"/> байт = НЕдополненное содержимое пакета
    /// (включая его varint-номер). Копируется внутри. Возвращает repair-шарды, когда
    /// блок из k пакетов заполнился, иначе null.
    /// </summary>
    public Repair[]? Add(ulong pn, ReadOnlySpan<byte> data, int len)
    {
        lock (_lock)
        {
            _block.Add((pn, [.. data[..len]], len));
            if (_block.Count < _k) return null;

            int shardSize = 2 + _block.Max(b => b.Len);
            var shards = new byte[_k][];
            var members = new FecMember[_k];
            for (int i = 0; i < _k; i++)
            {
                var shard = new byte[shardSize];
                BinaryPrimitives.WriteUInt16BigEndian(shard, (ushort)_block[i].Len);
                Array.Copy(_block[i].Data, 0, shard, 2, _block[i].Len);
                shards[i] = shard;
                members[i] = new FecMember(_block[i].Pn, _block[i].Len);
            }

            byte[][] parity = new ReedSolomon(_k, _m).Encode(shards);
            ulong blockId = _nextBlockId++;
            _block.Clear();

            var result = new Repair[_m];
            for (int j = 0; j < _m; j++)
                result[j] = new Repair(blockId, _m, j, shardSize, members, parity[j]);
            return result;
        }
    }
}