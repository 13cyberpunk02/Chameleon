using System.Buffers.Binary;
using Chameleon.Core.Protocol;

namespace Chameleon.Core.Fec;

/// <summary>
/// Приёмная сторона FEC: помнит недавно полученные пакеты и, получив repair-шард,
/// пытается восстановить недостающие пакеты блока. Восстановление - чистое ускорение
/// поверх ACK/ретрансмитов: если шардов не хватает, просто ничего не возвращает, а
/// пакет позже доедет ретрансмитом. Потокобезопасен.
/// </summary>
public sealed class FecDecoder(int recentCapacity = 1024, int blockCapacity = 64)
{
    private readonly object _lock = new();

    private readonly Dictionary<ulong, byte[]> _recent = new();
    private readonly Queue<ulong> _recentOrder = new();

    private sealed class Block
    {
        public int K, M, ShardSize;
        public FecMember[] Members = [];
        public readonly Dictionary<int, byte[]> Parity = new();
        public bool Done;
    }

    private readonly Dictionary<ulong, Block> _blocks = new();
    private readonly Queue<ulong> _blockOrder = new();

    /// <summary>Запомнить полученный пакет (сырые расшифрованные байты записи).</summary>
    public void Remember(ulong pn, ReadOnlySpan<byte> raw)
    {
        lock (_lock)
        {
            if (!_recent.TryAdd(pn, raw.ToArray())) return;
            _recentOrder.Enqueue(pn);
            while (_recentOrder.Count > recentCapacity)
                _recent.Remove(_recentOrder.Dequeue());
        }
    }

    /// <summary>
    /// Обработать repair-шард. Возвращает восстановленные Недополненные плейнтексты
    /// недостающих пакетов блока (может быть пусто).
    /// </summary>
    public List<byte[]> OnRepair(FecRepairFrame f)
    {
        var recovered = new List<byte[]>();
        lock (_lock)
        {
            if (!_blocks.TryGetValue(f.BlockId, out var b))
            {
                b = new Block
                {
                    K = f.Members.Count,
                    M = f.ParityCount,
                    ShardSize = f.ShardSize,
                    Members = f.Members as FecMember[] ?? [.. f.Members],
                };
                _blocks[f.BlockId] = b;
                _blockOrder.Enqueue(f.BlockId);
                while (_blockOrder.Count > blockCapacity)
                    _blocks.Remove(_blockOrder.Dequeue());
            }

            if (b.Done || b.K < 1 || b.ShardSize < 2) return recovered;
            if (f.ShardSize != b.ShardSize || f.Members.Count != b.K) return recovered;
            if (f.ShardIndex < 0 || f.ShardIndex >= b.M) return recovered;

            if (f.Parity.Length >= b.ShardSize)
                b.Parity[f.ShardIndex] = f.Parity.Span[..b.ShardSize].ToArray();

            int k = b.K, m = b.M, size = b.ShardSize;

            var present = new bool[k + m];
            var missingData = new List<int>();
            int have = 0;
            for (int i = 0; i < k; i++)
            {
                if (_recent.ContainsKey(b.Members[i].PacketNumber))
                {
                    present[i] = true;
                    have++;
                }
                else missingData.Add(i);
            }

            if (missingData.Count == 0)
            {
                b.Done = true;
                return recovered;
            }

            for (int j = 0; j < m; j++)
                if (b.Parity.ContainsKey(j))
                {
                    present[k + j] = true;
                    have++;
                }

            if (have < k) return recovered;

            var shards = new byte[k + m][];
            for (int i = 0; i < k; i++)
            {
                var shard = new byte[size];
                if (present[i])
                {
                    byte[] raw = _recent[b.Members[i].PacketNumber];
                    int len = b.Members[i].Length;
                    if (len < 0 || len > size - 2)
                    {
                        present[i] = false;
                        have--;
                        missingData.Add(i);
                    }
                    else
                    {
                        BinaryPrimitives.WriteUInt16BigEndian(shard, (ushort)len);
                        Array.Copy(raw, 0, shard, 2, Math.Min(len, raw.Length));
                    }
                }

                shards[i] = shard;
            }

            if (have < k) return recovered;
            for (int j = 0; j < m; j++)
                shards[k + j] = present[k + j] ? b.Parity[j] : new byte[size];

            try
            {
                new ReedSolomon(k, m).DecodeMissingData(shards, present);
            }
            catch
            {
                return recovered;
            }

            recovered.AddRange(from i in missingData let len = BinaryPrimitives.ReadUInt16BigEndian(shards[i]) where len >= 0 && len <= size - 2 select shards[i].AsSpan(2, len).ToArray());

            b.Done = true;
        }

        return recovered;
    }
}