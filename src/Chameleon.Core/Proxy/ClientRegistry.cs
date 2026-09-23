using System.Collections.Concurrent;
using System.Text.Json;

namespace Chameleon.Core.Proxy;

/// <summary>Аккаунт клиента: публичный ключ (идентичность) + имя.</summary>
public sealed class ClientAccount
{
    public string PublicKeyHex { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public DateTime AddedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Список разрешённых клиентов (по публичному ключу). Если <see cref="Enforced"/>
/// выключен - сервер принимает любого (как раньше). Если включён - только ключи
/// из списка (и только Enabled). Управляется файлом clients.json и (позже) API.
/// </summary>
public sealed class ClientRegistry
{
    private readonly ConcurrentDictionary<string, ClientAccount> _byKey = new();
    private string? _path;

    /// <summary>true - пускать только из списка; false - любого.</summary>
    public bool Enforced { get; set; }

    public bool IsAllowed(string publicKeyHex)
    {
        if (!Enforced) return true;
        return _byKey.TryGetValue(publicKeyHex.ToLowerInvariant(), out var a) && a.Enabled;
    }

    public void Add(ClientAccount account)
    {
        account.PublicKeyHex = account.PublicKeyHex.ToLowerInvariant();
        _byKey[account.PublicKeyHex] = account;
        Save();
    }

    public bool Remove(string publicKeyHex)
    {
        bool ok = _byKey.TryRemove(publicKeyHex.ToLowerInvariant(), out _);
        if (ok) Save();
        return ok;
    }

    public bool SetEnabled(string publicKeyHex, bool enabled)
    {
        if (!_byKey.TryGetValue(publicKeyHex.ToLowerInvariant(), out var a)) return false;
        a.Enabled = enabled;
        Save();
        return true;
    }

    public IReadOnlyList<ClientAccount> List() => _byKey.Values.OrderBy(a => a.Name).ToList();
    public int Count => _byKey.Count;
    
    private sealed record FileModel(bool Enforced, List<ClientAccount> Clients);

    public static ClientRegistry Load(string path, bool enforcedDefault)
    {
        var reg = new ClientRegistry { _path = path, Enforced = enforcedDefault };
        try
        {
            if (File.Exists(path))
            {
                var model = JsonSerializer.Deserialize<FileModel>(File.ReadAllText(path));
                if (model is not null)
                {
                    reg.Enforced = model.Enforced || enforcedDefault;
                    foreach (var c in model.Clients) reg._byKey[c.PublicKeyHex.ToLowerInvariant()] = c;
                }
            }
        }
        catch
        {
            // ignored
        }

        return reg;
    }

    public void Save()
    {
        if (_path is null) return;
        try
        {
            string? dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var model = new FileModel(Enforced, _byKey.Values.ToList());
            File.WriteAllText(_path,
                JsonSerializer.Serialize(model, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // ignored
        }
    }
}