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

    /// <summary>Идёт подключение/переподключение - для анимации (дуга-спиннер и волны).</summary>
    [ObservableProperty] private bool _isConnecting;

    /// <summary>Короткий импульс после отключения - кнопка «оседает» (анимация отключения).</summary>
    [ObservableProperty] private bool _isSettling;

    // Страна сервера: из кэша профиля до подключения, уточняется после.
    [ObservableProperty] private string? _countryCode;
    [ObservableProperty] private string _countryName = "";

    public Geometry PowerIcon => PageIcons.Power;

    /// <summary>Профиль, с которым идёт текущее подключение (ему запоминаем страну).</summary>
    private ServerProfile? _sessionProfile;

    protected override void OnActivated() => RefreshProfile();

    partial void OnIsBusyChanged(bool value) => UpdateConnecting();

    private void UpdateConnecting() =>
        IsConnecting = IsBusy || _tunnel.Status is TunnelStatus.Connecting or TunnelStatus.Reconnecting;

    /// <summary>Имя и страна выбранного профиля. Раньше показывалось «профиль не выбран», пока не нажмёшь кнопку.</summary>
    private void RefreshProfile()
    {
        if (IsConnected || IsBusy) return;
        var p = _store.Selected;
        ActiveProfile = p?.Display ?? "Сервер не выбран - добавьте его на вкладке «Серверы»";
        CountryCode = p?.CountryCode;
        CountryName = p?.CountryName ?? "";
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
            _sessionProfile = profile;
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
        UpdateConnecting();
        ErrorText = s == TunnelStatus.Error ? _tunnel.LastError ?? "Подробности - на вкладке «Логи»" : "";
        if (!IsConnected) RefreshProfile();

        if (s == TunnelStatus.Disconnected && _lastStatus is TunnelStatus.Connected or TunnelStatus.Reconnecting)
            _ = SettleAsync();

        // Переподключение - продолжение той же сессии: время не сбрасываем.
        if (s == TunnelStatus.Connected) StartStats(resetUptime: _lastStatus != TunnelStatus.Reconnecting);
        else if (s is TunnelStatus.Disconnected or TunnelStatus.Error) StopStats();
        _lastStatus = s;
    }

    private async Task SettleAsync()
    {
        IsSettling = true;
        await Task.Delay(700);
        IsSettling = false;
    }

    private void StartStats(bool resetUptime)
    {
        if (resetUptime) _connectedAt = DateTime.Now;
        _statusTimer ??= new Timer(_ => Avalonia.Threading.Dispatcher.UIThread.Post(UpdateStats), null,
            0, 1000);
        _ = RefreshExitAsync();
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

    /// <summary>IP и страна выхода (через туннель). Страна запоминается в профиле - флаг будет
    /// виден в списке серверов и на главной и до следующего подключения.</summary>
    private async Task RefreshExitAsync()
    {
        string socks = string.IsNullOrWhiteSpace(_store.Socks) ? "127.0.0.1:1080" : _store.Socks;
        var info = await Chameleon.Core.Proxy.ExitGeo.LookupAsync(socks, TimeSpan.FromSeconds(8));
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (!IsConnected) return;
            PublicIp = info?.Ip ?? "-";
            if (info?.CountryCode is not { } code) return;

            CountryCode = code;
            CountryName = info.CountryName ?? code.ToUpperInvariant();
            if (_sessionProfile is { } p && (p.CountryCode != code || p.CountryName != info.CountryName))
            {
                p.CountryCode = code;
                p.CountryName = info.CountryName;
                _store.Save();
            }
        });
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