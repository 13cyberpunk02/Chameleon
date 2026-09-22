using Avalonia.Media;
using Chameleon.Gui.Common;
using Chameleon.Tunnel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Chameleon.Gui.ViewModels;

/// <summary>Страница «Подключение»: большая кнопка, статус, статистика.</summary>
public sealed partial class ConnectViewModel : PageViewModel
{
    public override string Title => "Подключение";
    public override string Icon => "🔌";

    private readonly TunnelService _tunnel = Services.Tunnel;
    private readonly ProfileStore _store = Services.Store;
    private System.Threading.Timer? _statusTimer;
    private DateTime _connectedAt;

    public ConnectViewModel()
    {
        _tunnel.StatusChanged += (_, s) => Avalonia.Threading.Dispatcher.UIThread.Post(() => ApplyStatus(s));
        ApplyStatus(_tunnel.Status);
    }
    
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = "Отключено";
    [ObservableProperty] private IBrush _statusColor = Brush.Parse("#8B949E");
    [ObservableProperty] private string _buttonText = "Подключить";

    [ObservableProperty] private string _publicIp = "-";
    [ObservableProperty] private string _rtt = "-";
    [ObservableProperty] private string _trafficUp = "-";
    [ObservableProperty] private string _trafficDown = "-";
    [ObservableProperty] private string _uptime = "-";
    [ObservableProperty] private string _carriers = "-";
    [ObservableProperty] private string _activeProfile = "профиль не выбран";

    private bool CanToggle => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanToggle))]
    private async Task ToggleAsync()
    {
        if (IsConnected)
        {
            await _tunnel.DisconnectAsync();
            return;
        }

        var profile = _store.Selected;
        if (profile is null || string.IsNullOrWhiteSpace(profile.Server) ||
            string.IsNullOrWhiteSpace(profile.ServerPublicKeyHex))
        {
            StatusText = "Выберите сервер на вкладке «Серверы»";
            return;
        }

        IsBusy = true;
        ToggleCommand.NotifyCanExecuteChanged();
        try
        {
            var options = new TunnelOptions
            {
                Server = profile.Server,
                ServerPublicKeyHex = profile.ServerPublicKeyHex,
                Sni = string.IsNullOrWhiteSpace(profile.Sni) ? profile.Server.Split(':')[0] : profile.Sni,
                SocksListen = string.IsNullOrWhiteSpace(_store.Socks) ? "127.0.0.1:1080" : _store.Socks,
                Tun2SocksPath = string.IsNullOrWhiteSpace(_store.Tun2SocksPath)
                    ? (OperatingSystem.IsWindows() ? "tun2socks.exe" : "tun2socks")
                    : _store.Tun2SocksPath,
                Tun2SocksLogLevel = string.IsNullOrWhiteSpace(_store.LogLevel) ? "error" : _store.LogLevel,
                BypassRules = _store.BypassRules,
            };
            ActiveProfile = profile.Display;
            await _tunnel.ConnectAsync(options);
        }
        catch (Exception)
        {
            /* статус придёт через StatusChanged (Error) */
        }
        finally
        {
            IsBusy = false;
            ToggleCommand.NotifyCanExecuteChanged();
        }
    }

    private void ApplyStatus(TunnelStatus s)
    {
        (StatusText, var color, ButtonText, IsConnected) = s switch
        {
            TunnelStatus.Connected => ("Подключено", "#3FB950", "Отключить", true),
            TunnelStatus.Connecting => ("Подключение…", "#D29922", "Отмена", false),
            TunnelStatus.Reconnecting => ("Переподключение…", "#D29922", "Отключить", true),
            TunnelStatus.Error => ("Ошибка", "#F85149", "Подключить", false),
            _ => ("Отключено", "#8B949E", "Подключить", false),
        };
        StatusColor = Brush.Parse(color);
        ToggleCommand.NotifyCanExecuteChanged();

        if (s == TunnelStatus.Connected) StartStats();
        else if (s is TunnelStatus.Disconnected or TunnelStatus.Error) StopStats();
    }

    private void StartStats()
    {
        _connectedAt = DateTime.Now;
        _statusTimer ??= new System.Threading.Timer(_ => Avalonia.Threading.Dispatcher.UIThread.Post(UpdateStats), null,
            0, 1000);
        _ = RefreshIpAsync();
    }

    private void StopStats()
    {
        _statusTimer?.Dispose();
        _statusTimer = null;
        PublicIp = Rtt = TrafficUp = TrafficDown = Uptime = Carriers = "-";
    }

    private void UpdateStats()
    {
        var c = _tunnel.Client;
        if (c is null) return;
        Rtt = c.RttMs > 0 ? $"{c.RttMs} ms" : "-";
        Carriers = c.CarrierCount.ToString();
        TrafficUp = Human(c.BytesSent);
        TrafficDown = Human(c.BytesReceived);
        var t = DateTime.Now - _connectedAt;
        Uptime = t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes:00}:{t.Seconds:00}";
    }

    private async Task RefreshIpAsync()
    {
        try
        {
            string socks = string.IsNullOrWhiteSpace(_store.Socks) ? "127.0.0.1:1080" : _store.Socks;
            using var handler = new HttpClientHandler
                { Proxy = new System.Net.WebProxy($"socks5://{socks}"), UseProxy = true };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
            string ip = (await http.GetStringAsync("https://api.ipify.org")).Trim();
            Avalonia.Threading.Dispatcher.UIThread.Post(() => PublicIp = ip);
        }
        catch
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => PublicIp = "-");
        }
    }

    private static string Human(long bytes)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        int k = 0;
        while (v >= 1024 && k < u.Length - 1)
        {
            v /= 1024;
            k++;
        }

        return $"{v:0.#} {u[k]}";
    }
}