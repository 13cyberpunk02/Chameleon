using System;
using System.Threading.Tasks;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Chameleon.Mobile.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    [ObservableProperty] private string _link = "";
    [ObservableProperty] private string _statusText = "Отключено";
    [ObservableProperty] private IBrush _statusColor = Brush.Parse("#8B949E");
    [ObservableProperty] private string _buttonText = "Подключить";
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private string _hint = "";

    public Func<Task<string?>>? GetClipboard { get; set; }

    public MainViewModel()
    {
        Link = MobileStore.LoadLink();
        Vpn.StatusChanged += ApplyStatus;
        ApplyStatus(Vpn.Status);
    }

    [RelayCommand]
    private void Toggle()
    {
        if (IsConnected || Vpn.Status == VpnStatus.Connecting)
        {
            Vpn.Current?.Disconnect();
            return;
        }
        string link = Link.Trim();
        if (!link.StartsWith("chameleon://"))
        {
            Hint = "Вставьте ссылку chameleon:// (её даёт администратор)";
            return;
        }
        MobileStore.SaveLink(link);
        Hint = "";
        Vpn.Current?.Connect(link);
    }

    [RelayCommand]
    private async Task PasteAsync()
    {
        if (GetClipboard is null) return;
        string? text = await GetClipboard();
        if (!string.IsNullOrWhiteSpace(text) && text.Trim().StartsWith("chameleon://"))
        {
            Link = text.Trim();
            MobileStore.SaveLink(Link);
            Hint = "Ссылка вставлена";
        }
        else Hint = "В буфере нет ссылки chameleon://";
    }

    private void ApplyStatus(VpnStatus s)
    {
        string color;
        (StatusText, color, ButtonText, IsConnected) = s switch
        {
            VpnStatus.Connected    => ("Подключено", "#3FB950", "Отключить", true),
            VpnStatus.Connecting   => ("Подключение…", "#D29922", "Отмена", false),
            VpnStatus.Error        => ("Ошибка", "#F85149", "Подключить", false),
            _                       => ("Отключено", "#8B949E", "Подключить", false),
        };
        StatusColor = Brush.Parse(color);
    }
}
