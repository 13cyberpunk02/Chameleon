using System.Security.Cryptography;
using System.Text.Json;
using Chameleon.Core.Crypto;
using Chameleon.Core.Proxy;
using Chameleon.Gui.Settings;
using Chameleon.Tunnel;

namespace Chameleon.Gui;

/// <summary>Один профиль сервера.</summary>
public sealed class ServerProfile
{
    public string Name { get; set; } = "";
    public string Server { get; set; } = "";
    public string ServerPublicKeyHex { get; set; } = "";
    public string Sni { get; set; } = "www.example-cdn.com";

    public string Display => string.IsNullOrWhiteSpace(Name) ? Server : Name;

    public static ServerProfile FromLink(ChameleonLink link) => new()
    {
        Name = link.Name ?? link.Host,
        Server = $"{link.Host}:{link.Port}",
        ServerPublicKeyHex = link.ServerPublicKeyHex,
        Sni = link.Sni,
    };

    public string ToLink()
    {
        int i = Server.LastIndexOf(':');
        string host = i > 0 ? Server[..i] : Server;
        int port = i > 0 && int.TryParse(Server[(i + 1)..], out int p) ? p : 443;
        return new ChameleonLink(host, port, ServerPublicKeyHex,
            string.IsNullOrWhiteSpace(Sni) ? host : Sni,
            [], string.IsNullOrWhiteSpace(Name) ? null : Name).Build();
    }
}

/// <summary>Хранилище: список профилей + общие настройки (tun2socks, автозапуск и т.п.).</summary>
public sealed class ProfileStore
{
    public List<ServerProfile> Profiles { get; set; } = [];
    public int SelectedIndex { get; set; } = 0;

    // Общие (не привязаны к серверу)
    public string Socks { get; set; } = "127.0.0.1:1080";
    public string Tun2SocksPath { get; set; } = "";
    public string LogLevel { get; set; } = "error";
    public bool AutoConnect { get; set; } = false;
    public List<BypassRule> BypassRules { get; set; } = [];
    public string ClientPrivateKeyHex { get; set; } = "";

    /// <summary>Публичный ключ клиента (идентичность для allowlist сервера).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string ClientPublicKeyHex =>
        string.IsNullOrEmpty(ClientPrivateKeyHex)
            ? ""
            : Convert.ToHexString(X25519.ScalarMultBase(Convert.FromHexString(ClientPrivateKeyHex))).ToLowerInvariant();

    private static string Path => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Chameleon", "profiles.json");

    private static string LegacyPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Chameleon", "settings.json");

    public static ProfileStore Load()
    {
        ProfileStore store;
        try
        {
            store = File.Exists(Path)
                ? JsonSerializer.Deserialize<ProfileStore>(File.ReadAllText(Path)) ?? Migrate()
                : Migrate();
        }
        catch
        {
            store = Migrate();
        }

        if (string.IsNullOrEmpty(store.ClientPrivateKeyHex))
        {
            store.ClientPrivateKeyHex = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            store.Save();
        }

        return store;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // ignored
        }
    }

    /// <summary>Миграция со старого settings.json (один сервер) в список профилей.</summary>
    private static ProfileStore Migrate()
    {
        var store = new ProfileStore();
        try
        {
            if (File.Exists(LegacyPath))
            {
                var old = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(LegacyPath));
                if (old is not null)
                {
                    store.Socks = old.Socks;
                    store.Tun2SocksPath = old.Tun2SocksPath;
                    store.LogLevel = old.LogLevel;
                    store.AutoConnect = old.AutoConnect;
                    if (!string.IsNullOrWhiteSpace(old.Server) && !string.IsNullOrWhiteSpace(old.ServerPublicKeyHex))
                        store.Profiles.Add(new ServerProfile
                        {
                            Name = "Сервер", Server = old.Server,
                            ServerPublicKeyHex = old.ServerPublicKeyHex, Sni = old.Sni
                        });
                }
            }
        }
        catch
        {
            // ignored
        }

        return store;
    }

    public ServerProfile? Selected =>
        SelectedIndex >= 0 && SelectedIndex < Profiles.Count ? Profiles[SelectedIndex] : null;
}