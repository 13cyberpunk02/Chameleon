using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Chameleon.Mobile.ViewModels;

public partial class ConnectPageViewModel : ViewModelBase
{
    private readonly MobileProfiles _store;

    [ObservableProperty] private string _statusText = "Отключено";
    [ObservableProperty] private IBrush _statusColor = Brush.Parse("#8B949E");
    [ObservableProperty] private string _profileName = "Профиль не выбран";
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private string _hint = "";
    [ObservableProperty] private string _trafficDown = "0 B";
    [ObservableProperty] private string _trafficUp = "0 B";
    [ObservableProperty] private string _rtt = "-";
    [ObservableProperty] private string _publicIp = "-";
    [ObservableProperty] private bool _showStats;
    [ObservableProperty] private bool _isPulsing;

    // Состояния для анимации кнопки (классы «сцены» в ConnectPage.axaml).
    [ObservableProperty] private bool _isConnecting;
    [ObservableProperty] private bool _isSettling;

    // Страна сервера: из кэша профиля до подключения, уточняется после.
    [ObservableProperty] private string? _countryCode;
    [ObservableProperty] private string _countryName = "";

    private VpnStatus _lastStatus = VpnStatus.Disconnected;

    /// <summary>Профиль, с которым идёт подключение (ему запоминаем страну).</summary>
    private MobileProfile? _sessionProfile;

    public ConnectPageViewModel(MobileProfiles store)
    {
        _store = store;
        RefreshProfile();
        Vpn.StatusChanged += ApplyStatus;
        Vpn.StatsChanged += ApplyStats;
        Vpn.IpChanged += ip => PublicIp = ip;
        Vpn.CountryChanged += ApplyCountry;
        ApplyStatus(Vpn.Status);
    }

    public void RefreshProfile()
    {
        ProfileName = _store.Active?.Name is { Length: > 0 } n ? n : "Профиль не выбран";
        if (IsConnected) return;
        CountryCode = _store.Active?.CountryCode;
        CountryName = _store.Active?.CountryName ?? "";
    }

    /// <summary>Страна выхода узнана через туннель: показать и запомнить в профиле.</summary>
    private void ApplyCountry(string code, string? name)
    {
        CountryCode = code;
        CountryName = name ?? code.ToUpperInvariant();
        var p = _sessionProfile ?? _store.Active;
        if (p is null || (p.CountryCode == code && p.CountryName == name)) return;
        p.CountryCode = code;
        p.CountryName = name;
        _store.Save();
    }

    private async System.Threading.Tasks.Task SettleAsync()
    {
        IsSettling = true;
        await System.Threading.Tasks.Task.Delay(700);
        IsSettling = false;
    }

    [RelayCommand]
    private void Toggle()
    {
        if (IsConnected || Vpn.Status == VpnStatus.Connecting)
        {
            Vpn.Current?.Disconnect();
            return;
        }

        var active = _store.Active;
        if (active is null || !active.Link.StartsWith("chameleon://"))
        {
            Hint = "Выберите сервер на вкладке «Серверы»";
            return;
        }

        Hint = "";
        _sessionProfile = active;
        Vpn.Current?.Connect(active.Link);
    }

    private void ApplyStatus(VpnStatus s)
    {
        string color;
        (StatusText, color, IsConnected) = s switch
        {
            VpnStatus.Connected => ("Подключено", "#3FB950", true),
            VpnStatus.Connecting => ("Подключение…", "#D29922", false),
            VpnStatus.Error => ("Ошибка", "#F85149", false),
            _ => ("Отключено", "#8B949E", false),
        };
        StatusColor = Brush.Parse(color);
        ShowStats = s == VpnStatus.Connected;
        IsPulsing = s is VpnStatus.Connected or VpnStatus.Connecting;
        IsConnecting = s == VpnStatus.Connecting;
        if (s == VpnStatus.Disconnected && _lastStatus == VpnStatus.Connected) _ = SettleAsync();
        if (s is VpnStatus.Disconnected or VpnStatus.Error)
        {
            _sessionProfile = null;
            RefreshProfile();
        }

        _lastStatus = s;
        if (s != VpnStatus.Connected)
        {
            TrafficDown = "0 B";
            TrafficUp = "0 B";
            Rtt = "-";
            PublicIp = "-";
        }
    }

    private void ApplyStats(VpnStats st)
    {
        TrafficDown = Human(st.BytesDown);
        TrafficUp = Human(st.BytesUp);
        Rtt = st.RttMs > 0 ? $"{st.RttMs} мс" : "-";
    }

    private static string Human(long b)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double v = b;
        int k = 0;
        while (v >= 1024 && k < u.Length - 1)
        {
            v /= 1024;
            k++;
        }

        return $"{(v < 10 && k > 0 ? v.ToString("0.0") : v.ToString("0"))} {u[k]}";
    }
}