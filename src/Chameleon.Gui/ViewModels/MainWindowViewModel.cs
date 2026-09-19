using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Chameleon.Core.Proxy;
using Chameleon.Gui.Settings;
using Chameleon.Tunnel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Chameleon.Gui.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly AppSettings _settings = AppSettings.Load();
    private TunnelService? _service;
    private CancellationTokenSource? _cts;

    public MainWindowViewModel()
    {
        Server = _settings.Server;
        ServerKey = _settings.ServerPublicKeyHex;
        Sni = _settings.Sni;
        Socks = _settings.Socks;
        Tun2SocksPath = _settings.Tun2SocksPath;
        LogLevel = _settings.LogLevel;
        AutoConnect = _settings.AutoConnect;

        if (string.IsNullOrWhiteSpace(Tun2SocksPath))
        {
            string? found = Tun2SocksLocator.Find();
            if (found is not null)
            {
                Tun2SocksPath = found;
                AppendLog($"tun2socks найден автоматически: {found}");
            }
        }
    }

    [ObservableProperty] private string _server = "";
    [ObservableProperty] private string _serverKey = "";
    [ObservableProperty] private string _sni = "";
    [ObservableProperty] private string _socks = "";
    [ObservableProperty] private string _tun2SocksPath = "";
    [ObservableProperty] private string _logLevel = "error";
    [ObservableProperty] private string _profileName = "";
    [ObservableProperty] private bool _autoConnect;

    [ObservableProperty] private string _status = "Отключено";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isConnected;

    public ObservableCollection<string> Log { get; } = new();

    /// <summary>Функции доступа к буферу обмена (устанавливает View).</summary>
    public Func<Task<string?>>? GetClipboard { get; set; }

    public Func<string, Task>? SetClipboard { get; set; }

    /// <summary>Диалог выбора файла tun2socks (устанавливает View).</summary>
    public Func<Task<string?>>? PickTun2SocksFile { get; set; }

    [RelayCommand]
    private async Task BrowseTun2SocksAsync()
    {
        if (PickTun2SocksFile is null) return;
        string? path = await PickTun2SocksFile();
        if (string.IsNullOrWhiteSpace(path)) return;
        Tun2SocksPath = path;
        if (!Tun2SocksLocator.HasWintunNextTo(path))
            AppendLog("ВНИМАНИЕ: рядом с tun2socks нет wintun.dll - положите её в ту же папку");
        else AppendLog($"tun2socks выбран: {path}");
    }

    [RelayCommand]
    private async Task CopyLogAsync()
    {
        if (SetClipboard is null) return;
        await SetClipboard(string.Join(Environment.NewLine, Log));
        AppendLog("Лог скопирован в буфер обмена");
    }

    /// <summary>Вызывается View после загрузки окна: автоподключение, если включено.</summary>
    public async Task TryAutoConnectAsync()
    {
        if (AutoConnect && !IsConnected && !IsBusy
            && !string.IsNullOrWhiteSpace(Server) && !string.IsNullOrWhiteSpace(ServerKey))
        {
            AppendLog("Автоподключение…");
            await ConnectAsync();
        }
    }

    [RelayCommand]
    private async Task PasteLinkAsync()
    {
        try
        {
            string? text = GetClipboard is null ? null : await GetClipboard();
            if (string.IsNullOrWhiteSpace(text))
            {
                AppendLog("Буфер обмена пуст");
                return;
            }

            if (!ChameleonLink.TryParse(text, out var link, out string? err) || link is null)
            {
                AppendLog("Не ссылка chameleon://: " + err);
                return;
            }

            Server = $"{link.Host}:{link.Port}";
            ServerKey = link.ServerPublicKeyHex;
            Sni = link.Sni;
            ProfileName = link.Name ?? "";
            AppendLog($"Профиль вставлен из ссылки: {(string.IsNullOrEmpty(ProfileName) ? Server : ProfileName)}");
        }
        catch (Exception ex)
        {
            AppendLog("Ошибка вставки: " + ex.Message);
        }
    }

    [RelayCommand]
    private async Task CopyLinkAsync()
    {
        try
        {
            int i = Server.LastIndexOf(':');
            if (i <= 0)
            {
                AppendLog("Заполните адрес сервера host:port");
                return;
            }

            string host = Server[..i];
            if (!int.TryParse(Server[(i + 1)..], out int port))
            {
                AppendLog("Неверный порт в адресе");
                return;
            }

            var link = new ChameleonLink(host, port, ServerKey.Trim(),
                string.IsNullOrWhiteSpace(Sni) ? host : Sni.Trim(),
                [], string.IsNullOrWhiteSpace(ProfileName) ? null : ProfileName.Trim());
            if (SetClipboard is not null) await SetClipboard(link.Build());
            AppendLog("Ссылка скопирована в буфер обмена");
        }
        catch (Exception ex)
        {
            AppendLog("Ошибка копирования: " + ex.Message);
        }
    }

    private bool CanConnect => !IsBusy && !IsConnected;
    private bool CanDisconnect => !IsBusy && IsConnected;

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        SaveSettings();
        IsBusy = true;
        AppendLog("Подключение…");
        UpdateCommands();
        try
        {
            _service = new TunnelService();
            _service.Log += (_, m) => AppendLog(m);
            _service.StatusChanged += (_, s) => Status = Localize(s);

            var options = new TunnelOptions
            {
                Server = Server.Trim(),
                ServerPublicKeyHex = ServerKey.Trim(),
                Sni = string.IsNullOrWhiteSpace(Sni) ? "www.example-cdn.com" : Sni.Trim(),
                SocksListen = string.IsNullOrWhiteSpace(Socks) ? "127.0.0.1:1080" : Socks.Trim(),
                Tun2SocksPath = string.IsNullOrWhiteSpace(Tun2SocksPath)
                    ? (OperatingSystem.IsWindows() ? "tun2socks.exe" : "tun2socks")
                    : Tun2SocksPath.Trim(),
                Tun2SocksLogLevel = string.IsNullOrWhiteSpace(LogLevel) ? "error" : LogLevel.Trim(),
            };

            _cts = new CancellationTokenSource();
            await _service.ConnectAsync(options, _cts.Token);
            IsConnected = true;
            AppendLog("Готово: весь трафик идёт через туннель.");
        }
        catch (Exception ex)
        {
            AppendLog("Ошибка: " + ex.Message);
            Status = "Ошибка";
            await SafeDisconnectAsync();
        }
        finally
        {
            IsBusy = false;
            UpdateCommands();
        }
    }

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private async Task DisconnectAsync()
    {
        IsBusy = true;
        AppendLog("Отключение…");
        UpdateCommands();
        await SafeDisconnectAsync();
        IsBusy = false;
        UpdateCommands();
    }

    private async Task SafeDisconnectAsync()
    {
        try
        {
            _cts?.Cancel();
            if (_service is not null) await _service.DisconnectAsync();
        }
        catch
        {
            // ignored
        }

        _service = null;
        _cts = null;
        IsConnected = false;
        if (Status != "Ошибка") Status = "Отключено";
    }

    private void UpdateCommands()
    {
        ConnectCommand.NotifyCanExecuteChanged();
        DisconnectCommand.NotifyCanExecuteChanged();
    }

    private void AppendLog(string message)
    {
        // из фонового потока - в UI-поток
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Log.Add($"{DateTime.Now:HH:mm:ss}  {message}");
            while (Log.Count > 500) Log.RemoveAt(0);
        });
    }

    private void SaveSettings()
    {
        _settings.Server = Server;
        _settings.ServerPublicKeyHex = ServerKey;
        _settings.Sni = Sni;
        _settings.Socks = Socks;
        _settings.Tun2SocksPath = Tun2SocksPath;
        _settings.LogLevel = LogLevel;
        _settings.AutoConnect = AutoConnect;
        _settings.Save();
    }

    private static string Localize(TunnelStatus s) => s switch
    {
        TunnelStatus.Connecting => "Подключение…",
        TunnelStatus.Connected => "Подключено",
        TunnelStatus.Error => "Ошибка",
        _ => "Отключено",
    };

    partial void OnIsBusyChanged(bool value) => UpdateCommands();
    partial void OnIsConnectedChanged(bool value) => UpdateCommands();
}