using System.Collections.ObjectModel;
using Chameleon.Core.Proxy;
using Chameleon.Gui.Settings;
using Chameleon.Tunnel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Chameleon.Gui.ViewModels;

/// <summary>Только навигация: список страниц + текущая. Не хранит данные страниц.</summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    public ObservableCollection<PageViewModel> Pages { get; } =
    [
        new ConnectViewModel(),
        new ServersViewModel(),
        new LogsViewModel(),
        new RoutingViewModel(),
        new SettingsViewModel(),
    ];

    [ObservableProperty] private PageViewModel _current;

    public MainWindowViewModel() => Current = Pages[0];

    partial void OnCurrentChanged(PageViewModel value)
    {
        foreach (var p in Pages) p.IsActive = ReferenceEquals(p, value);
    }

    [RelayCommand]
    private void Navigate(PageViewModel page) => Current = page;
}