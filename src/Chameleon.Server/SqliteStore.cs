using System.Data;
using Chameleon.Core.Proxy;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Chameleon.Server;

/// <summary>
/// Единое SQLite-хранилище: аккаунты клиентов (allowlist) + накопленный трафик
/// + лимиты со СКОЛЬЗЯЩИМ периодом (30 дней от даты добавления клиента).
///
/// Реализует IClientStore (allowlist). Проверка лимита при подключении:
/// если download за текущий период >= limit_bytes (и limit>0) - не пускаем.
///
/// Периоды скользящие: period_start хранится на клиента; при наступлении нового
/// периода (сейчас > period_start + 30д) трафик периода обнуляется, period_start
/// сдвигается вперёд шагами по 30 дней.
///
/// Конкурентность: WAL + короткие транзакции. IsAllowed кэшируется в памяти
/// (горячий путь рукопожатия), кэш обновляется при изменениях.
/// </summary>
public sealed class SqliteStore : IClientStore, IDisposable
{
    private readonly string _connStr;
    private readonly bool _enforced;
    private static readonly TimeSpan Period = TimeSpan.FromDays(30);

    private volatile HashSet<string> _allowedCache = [];
    private readonly object _lock = new();

    public SqliteStore(string dbPath, bool enforced)
    {
        _enforced = enforced;
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _connStr = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        InitSchema();
        RefreshCache();
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connStr);
        c.Open();
        c.Execute("PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;");
        return c;
    }

    private void InitSchema()
    {
        using var c = Open();
        c.Execute(@"
            CREATE TABLE IF NOT EXISTS clients (
                key           TEXT PRIMARY KEY,
                name          TEXT NOT NULL DEFAULT '',
                enabled       INTEGER NOT NULL DEFAULT 1,
                added_utc     TEXT NOT NULL,
                limit_bytes   INTEGER NOT NULL DEFAULT 0,
                period_start  TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS traffic (
                key            TEXT PRIMARY KEY,
                total_down     INTEGER NOT NULL DEFAULT 0,
                total_up       INTEGER NOT NULL DEFAULT 0,
                period_down    INTEGER NOT NULL DEFAULT 0,
                period_up      INTEGER NOT NULL DEFAULT 0,
                last_seen_utc  TEXT
            );
        ");
    }

    public void MigrateFromJson(string clientsJson, string trafficJson)
    {
        using var c = Open();
        long clientCount = c.ExecuteScalar<long>("SELECT COUNT(*) FROM clients");
        if (clientCount > 0) return;

        try
        {
            if (File.Exists(clientsJson))
            {
                var model = System.Text.Json.JsonSerializer.Deserialize<LegacyClients>(File.ReadAllText(clientsJson));
                if (model?.Clients is not null)
                    foreach (var a in model.Clients)
                        AddInternal(c, a.PublicKeyHex, a.Name, a.Enabled,
                            a.AddedUtc == default ? DateTime.UtcNow : a.AddedUtc, 0);
            }

            if (File.Exists(trafficJson))
            {
                var t = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, LegacyTraffic>>(
                    File.ReadAllText(trafficJson));
                if (t is not null)
                    foreach (var kv in t)
                        c.Execute(@"INSERT INTO traffic(key,total_down,total_up,period_down,period_up,last_seen_utc)
                                    VALUES(@k,@d,@u,@d,@u,@s)
                                    ON CONFLICT(key) DO UPDATE SET total_down=@d,total_up=@u",
                            new
                            {
                                k = kv.Key.ToLowerInvariant(), d = kv.Value.Down, u = kv.Value.Up,
                                s = kv.Value.LastSeenUtc.ToString("o")
                            });
            }
        }
        catch
        {
            // ignored
        }

        RefreshCache();
    }

    private sealed class LegacyClients
    {
        public bool Enforced { get; set; }
        public List<ClientAccount> Clients { get; set; } = [];
    }

    private sealed class LegacyTraffic
    {
        public long Up { get; set; }
        public long Down { get; set; }
        public DateTime LastSeenUtc { get; set; }
    }

    public bool Enforced => _enforced;

    public int Count
    {
        get
        {
            using var c = Open();
            return (int)c.ExecuteScalar<long>("SELECT COUNT(*) FROM clients");
        }
    }

    /// <summary>Горячий путь: разрешён ли ключ (enabled и не превысил лимит). Из кэша.</summary>
    public bool IsAllowed(string publicKeyHex)
    {
        if (!_enforced) return true;
        return _allowedCache.Contains(publicKeyHex.ToLowerInvariant());
    }

    public IReadOnlyList<ClientAccount> List()
    {
        using var c = Open();
        return c.Query<ClientRow>("SELECT * FROM clients ORDER BY name").Select(r => r.ToAccount()).ToList();
    }

    public void Add(ClientAccount account)
    {
        using var c = Open();
        AddInternal(c, account.PublicKeyHex, account.Name, account.Enabled, DateTime.UtcNow, 0);
        RefreshCache();
    }

    private static void AddInternal(IDbConnection c, string key, string name, bool enabled, DateTime added,
        long limitBytes)
    {
        key = key.ToLowerInvariant();
        var now = DateTime.UtcNow;
        c.Execute(@"INSERT INTO clients(key,name,enabled,added_utc,limit_bytes,period_start)
                    VALUES(@key,@name,@en,@added,@lim,@ps)
                    ON CONFLICT(key) DO UPDATE SET name=@name",
            new
            {
                key, name, en = enabled ? 1 : 0, added = added.ToString("o"), lim = limitBytes, ps = now.ToString("o")
            });
        c.Execute("INSERT OR IGNORE INTO traffic(key) VALUES(@key)", new { key });
    }

    public bool Remove(string publicKeyHex)
    {
        using var c = Open();
        int n = c.Execute("DELETE FROM clients WHERE key=@k", new { k = publicKeyHex.ToLowerInvariant() });
        RefreshCache();
        return n > 0;
    }

    public bool SetEnabled(string publicKeyHex, bool enabled)
    {
        using var c = Open();
        int n = c.Execute("UPDATE clients SET enabled=@en WHERE key=@k",
            new { en = enabled ? 1 : 0, k = publicKeyHex.ToLowerInvariant() });
        RefreshCache();
        return n > 0;
    }

    public bool SetLimit(string publicKeyHex, long limitBytes)
    {
        using var c = Open();
        int n = c.Execute("UPDATE clients SET limit_bytes=@l WHERE key=@k",
            new { l = Math.Max(0, limitBytes), k = publicKeyHex.ToLowerInvariant() });
        RefreshCache();
        return n > 0;
    }

    /// <summary>Прибавить трафик завершённой сессии (down = клиенту, up = от клиента).</summary>
    public void AddClosedSession(string key, long down, long up)
    {
        key = key.ToLowerInvariant();
        using var c = Open();
        RollPeriodIfNeeded(c, key);
        c.Execute(@"INSERT INTO traffic(key,total_down,total_up,period_down,period_up,last_seen_utc)
                    VALUES(@k,@d,@u,@d,@u,@now)
                    ON CONFLICT(key) DO UPDATE SET
                        total_down=total_down+@d, total_up=total_up+@u,
                        period_down=period_down+@d, period_up=period_up+@u, last_seen_utc=@now",
            new { k = key, d = down, u = up, now = DateTime.UtcNow.ToString("o") });
        RefreshCacheKey(c, key);
    }

    /// <summary>Сдвиг скользящего периода: если истёк - обнуляем period_* и двигаем period_start.</summary>
    private void RollPeriodIfNeeded(IDbConnection c, string key)
    {
        var row = c.QueryFirstOrDefault<(string? ps, long lim)>(
            "SELECT period_start ps, limit_bytes lim FROM clients WHERE key=@k", new { k = key });
        if (row.ps is null) return;
        if (!DateTime.TryParse(row.ps, null, System.Globalization.DateTimeStyles.RoundtripKind, out var start)) return;

        var now = DateTime.UtcNow;
        if (now - start < Period) return;

        while (now - start >= Period) start += Period;
        c.Execute("UPDATE clients SET period_start=@ps WHERE key=@k", new { ps = start.ToString("o"), k = key });
        c.Execute("UPDATE traffic SET period_down=0, period_up=0 WHERE key=@k", new { k = key });
        RefreshCacheKey(c, key);
    }

    public sealed record TrafficRow(
        string Key,
        string Name,
        bool Enabled,
        DateTime AddedUtc,
        long LimitBytes,
        long TotalDown,
        long TotalUp,
        long PeriodDown,
        long PeriodUp,
        DateTime? LastSeenUtc,
        DateTime PeriodStart);

    /// <summary>Все клиенты с трафиком/лимитами (с учётом сдвига периода).</summary>
    public IReadOnlyList<TrafficRow> Rows()
    {
        using var c = Open();
        foreach (var k in c.Query<string>("SELECT key FROM clients")) RollPeriodIfNeeded(c, k);
        return c.Query<ClientRow>(@"SELECT c.*, t.total_down, t.total_up, t.period_down, t.period_up, t.last_seen_utc
                                    FROM clients c LEFT JOIN traffic t ON t.key=c.key ORDER BY c.name")
            .Select(r => r.ToTrafficRow()).ToList();
    }

    private void RefreshCache()
    {
        using var c = Open();
        RefreshCacheAll(c);
    }

    private void RefreshCacheAll(IDbConnection c)
    {
        var allowed = c.Query<string>(@"
            SELECT c.key FROM clients c LEFT JOIN traffic t ON t.key=c.key
            WHERE c.enabled=1 AND (c.limit_bytes=0 OR COALESCE(t.period_down,0) < c.limit_bytes)")
            .ToHashSet();
        lock (_lock) _allowedCache = allowed;
    }

    private void RefreshCacheKey(IDbConnection c, string key)
    {
        var ok = c.ExecuteScalar<long>(@"
            SELECT CASE WHEN c.enabled=1 AND (c.limit_bytes=0 OR COALESCE(t.period_down,0) < c.limit_bytes)
                        THEN 1 ELSE 0 END
            FROM clients c LEFT JOIN traffic t ON t.key=c.key WHERE c.key=@k", new { k = key });
        lock (_lock)
        {
            var set = new HashSet<string>(_allowedCache);
            if (ok == 1) set.Add(key);
            else set.Remove(key);
            _allowedCache = set;
        }
    }

    private sealed class ClientRow
    {
        public string key { get; set; } = "";
        public string name { get; set; } = "";
        public long enabled { get; set; }
        public string added_utc { get; set; } = "";
        public long limit_bytes { get; set; }
        public string period_start { get; set; } = "";
        public long? total_down { get; set; }
        public long? total_up { get; set; }
        public long? period_down { get; set; }
        public long? period_up { get; set; }
        public string? last_seen_utc { get; set; }

        public ClientAccount ToAccount() => new()
        {
            PublicKeyHex = key, Name = name, Enabled = enabled != 0,
            AddedUtc = Parse(added_utc) ?? DateTime.UtcNow,
        };

        public SqliteStore.TrafficRow ToTrafficRow() => new(
            key, name, enabled != 0, Parse(added_utc) ?? DateTime.UtcNow, limit_bytes,
            total_down ?? 0, total_up ?? 0, period_down ?? 0, period_up ?? 0,
            Parse(last_seen_utc), Parse(period_start) ?? DateTime.UtcNow);

        private static DateTime? Parse(string? s) =>
            DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d : null;
    }

    public void Dispose()
    {
    }
}