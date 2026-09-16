namespace Chameleon.Core.Fec;

/// <summary>
/// Стирающий код Рида-Соломона над GF(256). Систематический: первые k шардов -
/// это исходные данные, ещё m - избыточные (parity). Любые k из (k+m) шардов
/// восстанавливают все исходные - то есть можно потерять до m шардов и не ждать
/// ретрансмита. Матрица кодирования - Коши (любая её k×k подматрица обратима,
/// поэтому декодируется ЛЮБОЙ набор из k выживших).
///
/// Поле построено на примитивном полиноме 0x11d (стандарт для RS/AES-подобных).
/// </summary>
public sealed class ReedSolomon
{
    private static readonly byte[] Exp = new byte[512];
    private static readonly byte[] Log = new byte[256];

    static ReedSolomon()
    {
        int x = 1;
        for (int i = 0; i < 255; i++)
        {
            Exp[i] = (byte)x;
            Log[x] = (byte)i;
            x <<= 1;
            if ((x & 0x100) != 0) x ^= 0x11d;
        }

        for (int i = 255; i < 512; i++) Exp[i] = Exp[i - 255];
    }

    private readonly int _k;
    private readonly int _m;
    private readonly byte[][] _parityRows;

    public ReedSolomon(int dataShards, int parityShards)
    {
        if (dataShards < 1 || parityShards < 0 || dataShards + parityShards > 256)
            throw new ArgumentException("Требуется 1..255 data и data+parity ≤ 256");
        _k = dataShards;
        _m = parityShards;
        _parityRows = BuildCauchy(_k, _m);
    }

    public int DataShards => _k;
    public int ParityShards => _m;
    public int TotalShards => _k + _m;

    private static byte Mul(byte a, byte b) => a == 0 || b == 0 ? (byte)0 : Exp[Log[a] + Log[b]];
    private static byte Div(byte a, byte b) => a == 0 ? (byte)0 : Exp[Log[a] + 255 - Log[b]];

    /// <summary>Строки Коши: элемент[i][j] = 1/(x_i ⊕ y_j), x и y - непересекающиеся множества.</summary>
    private static byte[][] BuildCauchy(int k, int m)
    {
        var rows = new byte[m][];
        for (int i = 0; i < m; i++)
        {
            rows[i] = new byte[k];
            byte xi = (byte)(k + i);
            for (int j = 0; j < k; j++)
            {
                byte yj = (byte)j;
                rows[i][j] = Div(1, (byte)(xi ^ yj));
            }
        }

        return rows;
    }

    /// <summary>Считает m parity-шардов из k data-шардов (все шарды одной длины).</summary>
    public byte[][] Encode(byte[][] data)
    {
        if (data.Length != _k) throw new ArgumentException($"Ожидалось {_k} data-шардов");
        int size = data[0].Length;
        var parity = new byte[_m][];
        for (int i = 0; i < _m; i++)
        {
            var row = _parityRows[i];
            var outShard = new byte[size];
            for (int j = 0; j < _k; j++)
            {
                byte coeff = row[j];
                if (coeff == 0) continue;
                var src = data[j];
                for (int p = 0; p < size; p++)
                    outShard[p] ^= Mul(coeff, src[p]);
            }

            parity[i] = outShard;
        }

        return parity;
    }

    /// <summary>
    /// Восстанавливает стёртые data-шарды. <paramref name="shards"/> длины k+m
    /// (каждый - массив shardSize; стёртые могут быть нулевыми), <paramref
    /// name="present"/>[i] = false для стёртого. Нужно ≥ k присутствующих. После
    /// вызова стёртые data-шарды (индексы 0..k-1) заполнены.
    /// </summary>
    public void DecodeMissingData(byte[][] shards, bool[] present)
    {
        if (shards.Length != TotalShards || present.Length != TotalShards)
            throw new ArgumentException("Неверное число шардов");
        int shardSize = shards[FirstPresent(present)].Length;

        var rows = new byte[_k][];
        var vals = new byte[_k][];
        int taken = 0;
        for (int idx = 0; idx < TotalShards && taken < _k; idx++)
        {
            if (!present[idx]) continue;
            rows[taken] = FullMatrixRow(idx);
            vals[taken] = shards[idx];
            taken++;
        }

        if (taken < _k)
            throw new InvalidOperationException("Слишком много стёрто - восстановление невозможно");

        byte[][] inv = Invert(rows, _k);

        for (int r = 0; r < _k; r++)
        {
            if (present[r]) continue;
            var outShard = shards[r];
            Array.Clear(outShard, 0, shardSize);
            for (int j = 0; j < _k; j++)
            {
                byte coeff = inv[r][j];
                if (coeff == 0) continue;
                var src = vals[j];
                for (int p = 0; p < shardSize; p++)
                    outShard[p] ^= Mul(coeff, src[p]);
            }

            present[r] = true;
        }
    }

    private static int FirstPresent(bool[] present)
    {
        for (int i = 0; i < present.Length; i++)
            if (present[i])
                return i;
        throw new InvalidOperationException("Нет ни одного шарда");
    }

    private byte[] FullMatrixRow(int shardIndex)
    {
        var row = new byte[_k];
        if (shardIndex < _k) row[shardIndex] = 1;
        else row = (byte[])_parityRows[shardIndex - _k].Clone();
        return row;
    }

    private static byte[][] Invert(byte[][] matrix, int n)
    {
        var m = new byte[n][];
        var inv = new byte[n][];
        for (int i = 0; i < n; i++)
        {
            m[i] = (byte[])matrix[i].Clone();
            inv[i] = new byte[n];
            inv[i][i] = 1;
        }

        for (int col = 0; col < n; col++)
        {
            if (m[col][col] == 0)
            {
                int swap = col + 1;
                while (swap < n && m[swap][col] == 0) swap++;
                if (swap == n) throw new InvalidOperationException("Матрица вырождена");
                (m[col], m[swap]) = (m[swap], m[col]);
                (inv[col], inv[swap]) = (inv[swap], inv[col]);
            }

            byte pivot = m[col][col];
            for (int j = 0; j < n; j++)
            {
                m[col][j] = Div(m[col][j], pivot);
                inv[col][j] = Div(inv[col][j], pivot);
            }

            for (int row = 0; row < n; row++)
            {
                if (row == col) continue;
                byte factor = m[row][col];
                if (factor == 0) continue;
                for (int j = 0; j < n; j++)
                {
                    m[row][j] ^= Mul(factor, m[col][j]);
                    inv[row][j] ^= Mul(factor, inv[col][j]);
                }
            }
        }

        return inv;
    }
}