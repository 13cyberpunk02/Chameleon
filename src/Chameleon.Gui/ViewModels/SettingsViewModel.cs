using Chameleon.Gui.Common;
using Chameleon.Tunnel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Chameleon.Gui.ViewModels;

/// <summary>Страница «Настройки»: tun2socks, SOCKS, автозапуск, автоподключение, логи.</summary>
public sealed partial class SettingsViewModel : PageViewModel
{
    public override string Title => "Настройки";
    public override string Icon => "⚙";

    private readonly ProfileStore _store = Services.Store;

    /// <summary>Диалог выбора файла tun2socks задаёт View.</summary>
    public Func<Task<string?>>? PickTun2SocksFile { get; set; }

    [ObservableProperty] private string _tun2SocksPath;
    [ObservableProperty] private string _socks;
    [ObservableProperty] private string _logLevel;
    [ObservableProperty] private bool _autoConnect;
    [ObservableProperty] private bool _autoStart;
    [ObservableProperty] private string _hint = "";

    public string[] LogLevels { get; } = { "error", "warn", "info", "debug", "silent" };

    public SettingsViewModel()
    {
        _tun2SocksPath = _store.Tun2SocksPath;
        _socks = string.IsNullOrWhiteSpace(_store.Socks) ? "127.0.0.1:1080" : _store.Socks;
        _logLevel = string.IsNullOrWhiteSpace(_store.LogLevel) ? "error" : _store.LogLevel;
        _autoConnect = _store.AutoConnect;
        _autoStart = AutoStartManager.IsEnabled();

        if (string.IsNullOrWhiteSpace(_tun2SocksPath))
        {
            string? found = Tun2SocksLocator.Find();
            if (found is not null)
            {
                _tun2SocksPath = found;
                Hint = $"tun2socks найден автоматически: {found}";
            }
        }
    }

    [RelayCommand]
    private async Task BrowseTun2SocksAsync()
    {
        if (PickTun2SocksFile is null) return;
        string? path = await PickTun2SocksFile();
        if (string.IsNullOrWhiteSpace(path)) return;
        Tun2SocksPath = path;
        Hint = Tun2SocksLocator.HasWintunNextTo(path)
            ? $"tun2socks выбран: {path}"
            : "ВНИМАНИЕ: рядом с tun2socks нет wintun.dll - положите её в ту же папку";
    }

    // Автосохранение при изменении полей
    partial void OnTun2SocksPathChanged(string value)
    {
        _store.Tun2SocksPath = value;
        _store.Save();
    }

    partial void OnSocksChanged(string value)
    {
        _store.Socks = value;
        _store.Save();
    }

    partial void OnLogLevelChanged(string value)
    {
        _store.LogLevel = value;
        _store.Save();
    }

    partial void OnAutoConnectChanged(bool value)
    {
        _store.AutoConnect = value;
        _store.Save();
    }

    partial void OnAutoStartChanged(bool value)
    {
        try
        {
            AutoStartManager.Set(value);
            Hint = value ? "Автозапуск включён" : "Автозапуск выключен";
        }
        catch (Exception ex)
        {
            Hint = "Автозапуск: " + ex.Message;
        }
    }

    public bool AutoStartSupported => AutoStartManager.IsSupported;
}