using System.Collections.Concurrent;
using System.Text.Json;

namespace Chameleon.Server;

/// <summary>Накопленный трафик одного клиента (за всё время).</summary>
public sealed class ClientTraffic
{
    public long Up { get; set; }
    public long Down { get; set; }
    public DateTime LastSeenUtc { get; set; }
}

/// <summary>
/// Учёт трафика по КЛЮЧУ клиента, с сохранением в файл (переживает перезапуск).
///
/// Модель: в хранилище копится трафик ЗАВЕРШЁННЫХ сессий (через событие
/// SessionClosed). Трафик АКТИВНЫХ сессий добавляется «на лету» при выдаче (см.
/// LiveTotals в API) - так нет двойного счёта: закрылась сессия -> её трафик
/// переходит из «живого» в «сохранённый».
///
/// Абстрагирован ради будущей замены на SQLite (когда добавим историю/лимиты):
/// достаточно заменить реализацию хранения, интерфейс останется.
/// </summary>
public sealed class TrafficStore
{
    private readonly ConcurrentDictionary<string, ClientTraffic> _byKey = new();
    private readonly string _path;
    private readonly object _saveLock = new();
    private volatile bool _dirty;

    public TrafficStore(string path)
    {
        _path = path;
        Load();
        _ = Task.Run(async () =>
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
                if (_dirty) Save();
            }
        });
    }

    /// <summary>Прибавить трафик завершённой сессии к аккаунту клиента.</summary>
    public void AddClosedSession(string clientKeyHex, long down, long up)
    {
        string key = clientKeyHex.ToLowerInvariant();
        _byKey.AddOrUpdate(key,
            _ => new ClientTraffic { Up = up, Down = down, LastSeenUtc = DateTime.UtcNow },
            (_, t) =>
            {
                t.Up += up;
                t.Down += down;
                t.LastSeenUtc = DateTime.UtcNow;
                return t;
            });
        _dirty = true;
    }

    /// <summary>Сохранённый (завершённые сессии) трафик по ключу.</summary>
    public ClientTraffic Get(string clientKeyHex)
        => _byKey.TryGetValue(clientKeyHex.ToLowerInvariant(), out var t)
            ? new ClientTraffic { Up = t.Up, Down = t.Down, LastSeenUtc = t.LastSeenUtc }
            : new ClientTraffic();

    /// <summary>Снимок всех аккаунтов трафика.</summary>
    public IReadOnlyDictionary<string, ClientTraffic> Snapshot()
        => _byKey.ToDictionary(kv => kv.Key,
            kv => new ClientTraffic { Up = kv.Value.Up, Down = kv.Value.Down, LastSeenUtc = kv.Value.LastSeenUtc });

    private void Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var data = JsonSerializer.Deserialize<Dictionary<string, ClientTraffic>>(File.ReadAllText(_path));
                if (data is not null)
                    foreach (var kv in data)
                        _byKey[kv.Key] = kv.Value;
            }
        }
        catch
        {
            // ignored
        }
    }

    public void Save()
    {
        lock (_saveLock)
        {
            try
            {
                _dirty = false;
                string? dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                string tmp = _path + ".tmp";
                File.WriteAllText(tmp,
                    JsonSerializer.Serialize(Snapshot(), new JsonSerializerOptions { WriteIndented = true }));
                File.Move(tmp, _path, overwrite: true);
            }
            catch
            {
                // ignored
            }
        }
    }
}