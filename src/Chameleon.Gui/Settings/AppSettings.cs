using System;
using System.IO;
using System.Text.Json;

namespace Chameleon.Gui.Settings;

/// <summary>Сохраняемые настройки подключения (в %AppData%/Chameleon/settings.json).</summary>
public sealed class AppSettings
{
    public string Server { get; set; } = "";
    public string ServerPublicKeyHex { get; set; } = "";
    public string Sni { get; set; } = "www.example-cdn.com";
    public string Socks { get; set; } = "127.0.0.1:1080";
    public string Tun2SocksPath { get; set; } = "";
    public string LogLevel { get; set; } = "error";

    private static string Path => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Chameleon", "settings.json");

    public static AppSettings Load()
    {
        try { if (File.Exists(Path)) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path)) ?? new(); }
        catch { }
        return new();
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
}