using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Chameleon.Gui.ViewModels;

/// <summary>Только навигация: список страниц + текущая. Не хранит данные страниц.</summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    public ConnectViewModel Connect { get; } = new();

    public ObservableCollection<PageViewModel> Pages { get; }

    [ObservableProperty] private PageViewModel _current;

    public MainWindowViewModel()
    {
        Pages = [Connect, new ServersViewModel(), new LogsViewModel(), new RoutingViewModel(), new SettingsViewModel()];
        _current = Pages[0];
        Connect.IsActive = true;
    }

    partial void OnCurrentChanged(PageViewModel value)
    {
        foreach (var p in Pages) p.IsActive = ReferenceEquals(p, value);
    }

    [RelayCommand]
    private void Navigate(PageViewModel page) => Current = page;
}
