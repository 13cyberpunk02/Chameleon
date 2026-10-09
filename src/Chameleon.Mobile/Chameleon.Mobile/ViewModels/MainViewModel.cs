using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Chameleon.Mobile.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly MobileProfiles _store = MobileProfiles.Load();

    public ConnectPageViewModel Connect { get; }
    public ServersPageViewModel Servers { get; }
    public SettingsPageViewModel Settings { get; }

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsConnect), nameof(IsServers), nameof(IsSettings))]
    private ViewModelBase _current = null!;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsConnect), nameof(IsServers), nameof(IsSettings))]
    private string _tab = "connect";

    public bool IsConnect => Tab == "connect";
    public bool IsServers => Tab == "servers";
    public bool IsSettings => Tab == "settings";

    public MainViewModel()
    {
        Connect = new ConnectPageViewModel(_store);
        Servers = new ServersPageViewModel(_store);
        Settings = new SettingsPageViewModel(_store);
        Servers.ActiveChanged = () => Connect.RefreshProfile();
        Settings.ProfilesCleared = () =>
        {
            Servers.Reload();
            Connect.RefreshProfile();
        };
        Current = Connect;
        
        if (_store.AutoConnect && _store.Active is { } a && a.Link.StartsWith("chameleon://"))
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    Vpn.Current?.Connect(a.Link);
                }
                catch
                {
                    // ignored
                }
            }, Avalonia.Threading.DispatcherPriority.Background);
        }
    }

    [RelayCommand]
    private void Go(string tab)
    {
        Tab = tab;
        if (tab == "settings") Settings.Refresh();
        Current = tab switch
        {
            "servers" => Servers,
            "settings" => Settings,
            _ => Connect,
        };
    }
}