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

    public ConnectPageViewModel(MobileProfiles store)
    {
        _store = store;
        RefreshProfile();
        Vpn.StatusChanged += ApplyStatus;
        Vpn.StatsChanged += ApplyStats;
        Vpn.IpChanged += ip => PublicIp = ip;
        ApplyStatus(Vpn.Status);
    }

    public void RefreshProfile()
    {
        ProfileName = _store.Active?.Name is { Length: > 0 } n ? n : "Профиль не выбран";
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