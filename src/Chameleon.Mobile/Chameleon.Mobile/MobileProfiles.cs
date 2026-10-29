using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chameleon.Mobile;

/// <summary>Профиль сервера на мобиле: имя + chameleon:// ссылка.</summary>
public sealed class MobileProfile : INotifyPropertyChanged
{
    public string Name
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            OnPC();
        }
    } = string.Empty;

    public string Link
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            OnPC();
        }
    } = string.Empty;

    /// <summary>
    /// Страна сервера (ISO-код, «fi»), запомненная при последнем подключении - флаг виден
    /// в списке и на главной до подключения. Узнаётся только через туннель.
    /// </summary>
    public string? CountryCode
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            OnPC();
        }
    }

    public string? CountryName
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            OnPC();
        }
    }

    [JsonIgnore]
    public bool IsActive
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            OnPC();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPC([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

/// <summary>
/// Хранилище профилей (несколько серверов) + индекс активного. JSON в данных
/// приложения, переживает перезапуск. Аналог ProfileStore десктопа.
/// </summary>
public sealed class MobileProfiles
{
    public List<MobileProfile> Profiles { get; set; } = [];
    public int ActiveIndex { get; set; } = -1;
    public bool AutoConnect { get; set; }

    /// <summary>
    /// Быстрый режим: системный TLS Android вместо BouncyCastle. BouncyCastle на ARM
    /// шифрует программно (ускорение у него только под x86), системный TLS - аппаратным
    /// AES. Цена: ClientHello не совпадает с Chrome по JA4.
    /// </summary>
    public bool NativeTls { get; set; }
    public RoutingConfig Routing { get; set; } = new();

    private static string Path =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "profiles.json");

    public MobileProfile? Active =>
        ActiveIndex >= 0 && ActiveIndex < Profiles.Count ? Profiles[ActiveIndex] : null;

    public static MobileProfiles Load()
    {
        try
        {
            if (File.Exists(Path))
                return JsonSerializer.Deserialize<MobileProfiles>(File.ReadAllText(Path)) ?? new MobileProfiles();
        }
        catch
        {
            // ignored
        }

        return new MobileProfiles();
    }

    public void Save()
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(Path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // ignored
        }
    }
}