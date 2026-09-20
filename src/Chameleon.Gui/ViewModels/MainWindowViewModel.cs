using System.Collections.ObjectModel;
using Chameleon.Core.Proxy;
using Chameleon.Gui.Settings;
using Chameleon.Tunnel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Chameleon.Gui.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly ProfileStore _store = ProfileStore.Load();
    public System.Collections.ObjectModel.ObservableCollection<ServerProfile> Profiles { get; } = new();
    private TunnelService? _service;
    private CancellationTokenSource? _cts;

    public MainWindowViewModel()
    {
        Socks = _store.Socks;
        Tun2SocksPath = _store.Tun2SocksPath;
        LogLevel = _store.LogLevel;
        AutoConnect = _store.AutoConnect;
        AutoStart = AutoStartManager.IsEnabled();

        foreach (var p in _store.Profiles) Profiles.Add(p);
        SelectedProfile = _store.Selected ?? Profiles.FirstOrDefault();

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
    [ObservableProperty] private bool _autoStart;
    [ObservableProperty] private ServerProfile? _selectedProfile;

    partial void OnSelectedProfileChanged(ServerProfile? value)
    {
        if (value is null)
        {
            Server = ServerKey = "";
            ProfileName = "";
            return;
        }

        Server = value.Server;
        ServerKey = value.ServerPublicKeyHex;
        Sni = value.Sni;
        ProfileName = value.Name;
        _store.SelectedIndex = Profiles.IndexOf(value);
        SaveSettings();
    }

    [ObservableProperty] private string _status = "Отключено";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isConnected;

    [ObservableProperty] private string _publicIp = "-";
    [ObservableProperty] private string _rtt = "-";
    [ObservableProperty] private string _traffic = "-";
    [ObservableProperty] private string _carriers = "-";
    [ObservableProperty] private string _uptime = "-";
    private DateTime _connectedAt;
    private ChameleonClient? _client;
    private Timer? _statusTimer;

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

    /// <summary>Вызывается View после загрузки окна: авто подключение, если включено.</summary>
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
    private async Task AddProfileAsync()
    {
        try
        {
            string? text = GetClipboard is null ? null : await GetClipboard();
            if (!ChameleonLink.TryParse(text, out var link, out string? err) || link is null)
            {
                AppendLog("В буфере нет ссылки chameleon://: " + err);
                return;
            }

            var profile = ServerProfile.FromLink(link);
            Profiles.Add(profile);
            _store.Profiles.Add(profile);
            SelectedProfile = profile;
            SaveSettings();
            AppendLog($"Профиль добавлен: {profile.Display}");
        }
        catch (Exception ex)
        {
            AppendLog("Ошибка добавления: " + ex.Message);
        }
    }

    [RelayCommand]
    private void RemoveProfile()
    {
        if (SelectedProfile is null) return;
        string name = SelectedProfile.Display;
        int idx = Profiles.IndexOf(SelectedProfile);
        _store.Profiles.Remove(SelectedProfile);
        Profiles.Remove(SelectedProfile);
        SelectedProfile = Profiles.Count > 0 ? Profiles[Math.Min(idx, Profiles.Count - 1)] : null;
        SaveSettings();
        AppendLog($"Профиль удалён: {name}");
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
            _client = _service.Client;
            _connectedAt = DateTime.Now;
            IsConnected = true;
            AppendLog("Готово: весь трафик идёт через туннель.");
            StartStatusPolling();
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
        StopStatusPolling();
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
        _client = null;
        IsConnected = false;
        PublicIp = "-";
        Rtt = "-";
        Traffic = "-";
        Carriers = "-";
        Uptime = "-";
        if (Status != "Ошибка") Status = "Отключено";
    }

    private void UpdateCommands()
    {
        ConnectCommand.NotifyCanExecuteChanged();
        DisconnectCommand.NotifyCanExecuteChanged();
    }

    private void StartStatusPolling()
    {
        _statusTimer = new Timer(_ => Avalonia.Threading.Dispatcher.UIThread.Post(UpdateStatus), null, 0, 1000);
        _ = RefreshPublicIpAsync();
    }

    private void StopStatusPolling()
    {
        _statusTimer?.Dispose();
        _statusTimer = null;
    }

    private void UpdateStatus()
    {
        if (_client is null) return;
        Rtt = _client.RttMs > 0 ? $"{_client.RttMs} мс" : "-";
        Carriers = _client.CarrierCount.ToString();
        Traffic = $"↑ {Human(_client.BytesSent)}   ↓ {Human(_client.BytesReceived)}";
        var t = DateTime.Now - _connectedAt;
        Uptime = t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes:00}:{t.Seconds:00}";
    }

    private async Task RefreshPublicIpAsync()
    {
        try
        {
            int i = Socks.LastIndexOf(':');
            if (i <= 0) return;
            var handler = new HttpClientHandler
                { Proxy = new System.Net.WebProxy($"socks5://{Socks}"), UseProxy = true };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
            string ip = (await http.GetStringAsync("https://api.ipify.org")).Trim();
            Avalonia.Threading.Dispatcher.UIThread.Post(() => PublicIp = ip);
        }
        catch
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => PublicIp = "не удалось определить");
        }
    }

    private static string Human(long bytes)
    {
        string[] u = { "Б", "КБ", "МБ", "ГБ", "ТБ" };
        double v = bytes;
        int k = 0;
        while (v >= 1024 && k < u.Length - 1)
        {
            v /= 1024;
            k++;
        }

        return $"{v:0.#} {u[k]}";
    }

    private void AppendLog(string message)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Log.Add($"{DateTime.Now:HH:mm:ss}  {message}");
            while (Log.Count > 500) Log.RemoveAt(0);
        });
    }

    private void SaveSettings()
    {
        _store.Socks = Socks;
        _store.Tun2SocksPath = Tun2SocksPath;
        _store.LogLevel = LogLevel;
        _store.AutoConnect = AutoConnect;
        if (SelectedProfile is { } p)
        {
            p.Server = Server;
            p.ServerPublicKeyHex = ServerKey;
            p.Sni = Sni;
            p.Name = ProfileName;
        }

        _store.Save();
    }

    private static string Localize(TunnelStatus s) => s switch
    {
        TunnelStatus.Connecting => "Подключение…",
        TunnelStatus.Connected => "Подключено",
        TunnelStatus.Error => "Ошибка",
        _ => "Отключено",
    };

    partial void OnAutoStartChanged(bool value)
    {
        try
        {
            AutoStartManager.Set(value);
            AppendLog(value ? "Автозапуск включён" : "Автозапуск выключен");
        }
        catch (Exception ex)
        {
            AppendLog("Автозапуск: " + ex.Message);
        }
    }

    partial void OnIsBusyChanged(bool value) => UpdateCommands();
    partial void OnIsConnectedChanged(bool value) => UpdateCommands();
}