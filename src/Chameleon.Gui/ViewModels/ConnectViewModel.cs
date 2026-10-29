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
    public override Geometry Icon => PageIcons.Power;

    private readonly TunnelService _tunnel = Services.Tunnel;
    private readonly ProfileStore _store = Services.Store;
    private System.Threading.Timer? _statusTimer;
    private DateTime _connectedAt;
    private TunnelStatus _lastStatus = TunnelStatus.Disconnected;
    private CancellationTokenSource? _connectCts;

    public ConnectViewModel()
    {
        _tunnel.StatusChanged += (_, s) => Avalonia.Threading.Dispatcher.UIThread.Post(() => ApplyStatus(s));
        ApplyStatus(_tunnel.Status);
        RefreshProfile();
    }

    // Статус
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = "Отключено";
    [ObservableProperty] private IBrush _statusColor = Brush.Parse("#8B949E");
    [ObservableProperty] private string _buttonText = "Подключить";
    [ObservableProperty] private string _errorText = "";

    public Geometry PowerIcon => PageIcons.Power;

    protected override void OnActivated() => RefreshProfile();

    /// <summary>Имя выбранного профиля. Раньше показывалось «профиль не выбран», пока не нажмёшь кнопку.</summary>
    private void RefreshProfile()
    {
        if (IsConnected || IsBusy) return;
        ActiveProfile = _store.Selected?.Display ?? "Сервер не выбран - добавьте его на вкладке «Серверы»";
    }

    /// <summary>Автоподключение при запуске (настройка «Авто подключение при запуске»).</summary>
    public void ConnectOnStartup()
    {
        if (_store.Selected is null || IsConnected || IsBusy) return;
        ToggleCommand.Execute(null);
    }

    // Статистика
    [ObservableProperty] private string _publicIp = "-";
    [ObservableProperty] private string _rtt = "-";
    [ObservableProperty] private string _trafficUp = "-";
    [ObservableProperty] private string _trafficDown = "-";
    [ObservableProperty] private string _uptime = "-";
    [ObservableProperty] private string _carriers = "-";
    [ObservableProperty] private string _activeProfile = "";

    /// <summary>
    /// Одна кнопка на три действия: подключить / отменить идущее подключение / отключить.
    /// Раньше на время подключения команда блокировалась, и «Отмена» не нажималась.
    /// </summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ToggleAsync()
    {
        if (IsBusy)
        {
            _connectCts?.Cancel();
            return;
        }

        if (IsConnected)
        {
            await _tunnel.DisconnectAsync();
            return;
        }

        var profile = _store.Selected;
        if (profile is null || string.IsNullOrWhiteSpace(profile.Server) ||
            string.IsNullOrWhiteSpace(profile.ServerPublicKeyHex))
        {
            ErrorText = "Выберите сервер на вкладке «Серверы»";
            return;
        }

        IsBusy = true;
        ErrorText = "";
        using var cts = new CancellationTokenSource();
        _connectCts = cts;
        try
        {
            var options = new TunnelOptions
            {
                Server = profile.Server,
                ServerPublicKeyHex = profile.ServerPublicKeyHex,
                Sni = string.IsNullOrWhiteSpace(profile.Sni) ? profile.Server.Split(':')[0] : profile.Sni,
                SocksListen = string.IsNullOrWhiteSpace(_store.Socks) ? "127.0.0.1:1080" : _store.Socks,
                // Пустой путь - ищем рядом с программой/в PATH, а не в текущей папке
                // (при автозапуске это System32).
                Tun2SocksPath = string.IsNullOrWhiteSpace(_store.Tun2SocksPath)
                    ? Tun2SocksLocator.Find() ?? (OperatingSystem.IsWindows() ? "tun2socks.exe" : "tun2socks")
                    : _store.Tun2SocksPath,
                Tun2SocksLogLevel = string.IsNullOrWhiteSpace(_store.LogLevel) ? "error" : _store.LogLevel,
                BypassRules = _store.BypassRules,
                ClientPrivateKeyHex = string.IsNullOrWhiteSpace(profile.ClientPrivateKeyHex)
                    ? _store.ClientPrivateKeyHex
                    : profile.ClientPrivateKeyHex,
                ExtraCarriers = profile.ExtraCarriers,
                NativeTls = _store.NativeTls,
            };
            ActiveProfile = profile.Display;
            await _tunnel.ConnectAsync(options, cts.Token);
        }
        catch (Exception)
        {
            /* статус придёт через StatusChanged (Error/Disconnected), причина - в LastError */
        }
        finally
        {
            _connectCts = null;
            IsBusy = false;
            RefreshProfile();
        }
    }

    private void ApplyStatus(TunnelStatus s)
    {
        string color;
        (StatusText, color, ButtonText, IsConnected) = s switch
        {
            TunnelStatus.Connected => ("Подключено", "#3FB950", "Отключить", true),
            TunnelStatus.Connecting => ("Подключение…", "#D29922", "Отмена", false),
            TunnelStatus.Reconnecting => ("Переподключение…", "#D29922", "Отключить", true),
            TunnelStatus.Error => ("Ошибка", "#F85149", "Подключить", false),
            _ => ("Отключено", "#8B949E", "Подключить", false),
        };
        StatusColor = Brush.Parse(color);
        ErrorText = s == TunnelStatus.Error ? _tunnel.LastError ?? "Подробности - на вкладке «Логи»" : "";
        if (!IsConnected) RefreshProfile();

        // Переподключение - продолжение той же сессии: время не сбрасываем.
        if (s == TunnelStatus.Connected) StartStats(resetUptime: _lastStatus != TunnelStatus.Reconnecting);
        else if (s is TunnelStatus.Disconnected or TunnelStatus.Error) StopStats();
        _lastStatus = s;
    }

    private void StartStats(bool resetUptime)
    {
        if (resetUptime) _connectedAt = DateTime.Now;
        _statusTimer ??= new Timer(_ => Avalonia.Threading.Dispatcher.UIThread.Post(UpdateStats), null,
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
        string[] u = ["B", "KB", "MB", "GB", "TB"];
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