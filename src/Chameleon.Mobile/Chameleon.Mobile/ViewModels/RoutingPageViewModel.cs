using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Chameleon.Mobile.ViewModels;

/// <summary>Строка приложения в списке маршрутизации.</summary>
public sealed partial class AppRow : ViewModelBase
{
    public string Package { get; }
    public string Label { get; }
    public Bitmap? Icon { get; }

    [ObservableProperty] private bool _isChecked;
    private readonly Action<AppRow> _onToggle;

    public AppRow(string pkg, string label, Bitmap? icon, bool isChecked, Action<AppRow> onToggle)
    {
        Package = pkg;
        Label = label;
        Icon = icon;
        _isChecked = isChecked;
        _onToggle = onToggle;
    }

    partial void OnIsCheckedChanged(bool value) => _onToggle(this);
}

public partial class RoutingPageViewModel : ViewModelBase
{
    private readonly MobileProfiles _store;
    private readonly RoutingConfig _cfg;
    private List<AppRow> _all = [];

    public ObservableCollection<AppRow> Apps { get; } = [];
    public ObservableCollection<RouteRule> IpRules { get; } = [];

    [ObservableProperty] private int _mode;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private string _newIp = "";
    [ObservableProperty] private string _hint = "";
    [ObservableProperty] private bool _loadingApps;

    public bool IsAll => Mode == 0;
    public bool IsAllowed => Mode == 1;
    public bool IsDisallowed => Mode == 2;
    public bool AppsVisible => Mode != 0;
    public bool AppsListVisible => Mode != 0 && !LoadingApps;

    public RoutingPageViewModel(MobileProfiles store)
    {
        _store = store;
        _cfg = _store.Routing;
        _mode = (int)_cfg.Mode;

        foreach (var r in _cfg.IpRules)
        {
            Attach(r);
            IpRules.Add(r);
        }

        if (_cfg.Mode != AppRouteMode.All) LoadAppsAsync();
    }
    
    [RelayCommand]
    private void SetMode(string m)
    {
        if (int.TryParse(m, out int v)) Mode = v;
    }

    partial void OnModeChanged(int value)
    {
        OnPropertyChanged(nameof(IsAll));
        OnPropertyChanged(nameof(IsAllowed));
        OnPropertyChanged(nameof(IsDisallowed));
        OnPropertyChanged(nameof(AppsVisible));
        OnPropertyChanged(nameof(AppsListVisible));

        _cfg.Mode = (AppRouteMode)value;
        _store.Save();
        Hint = value switch
        {
            1 => "Через VPN пойдут только выбранные приложения",
            2 => "Через VPN пойдёт всё, кроме выбранных приложений",
            _ => "Весь трафик идёт через VPN",
        };

        if (value != 0 && _all.Count == 0 && !LoadingApps) LoadAppsAsync();
    }
    
    private void LoadAppsAsync()
    {
        LoadingApps = true;
        System.Threading.Tasks.Task.Run(() =>
        {
            var entries = Vpn.Apps?.GetInstalled() ?? [];
            var rows = new List<AppRow>(entries.Count);
            foreach (var e in entries)
            {
                Bitmap? bmp = null;
                if (e.Icon is { Length: > 0 })
                    try
                    {
                        bmp = new Bitmap(new MemoryStream(e.Icon));
                    }
                    catch
                    {
                        // ignored
                    }

                rows.Add(new AppRow(e.Package, e.Label, bmp, _cfg.Apps.Contains(e.Package), OnAppToggled));
            }

            Dispatcher.UIThread.Post(() =>
            {
                _all = rows;
                ApplyFilter();
                LoadingApps = false;
                Hint = rows.Count == 0
                    ? "Не удалось получить список приложений (нужно разрешение QUERY_ALL_PACKAGES)"
                    : "";
            });
        });
    }

    partial void OnLoadingAppsChanged(bool value) => OnPropertyChanged(nameof(AppsListVisible));

    partial void OnSearchChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        Apps.Clear();
        var q = (Search ?? "").Trim();
        foreach (var a in _all.Where(a => q.Length == 0
                                          || a.Label.Contains(q, StringComparison.OrdinalIgnoreCase)
                                          || a.Package.Contains(q, StringComparison.OrdinalIgnoreCase)))
        {
            Apps.Add(a);
        }
    }

    private void OnAppToggled(AppRow row)
    {
        if (row.IsChecked)
        {
            if (!_cfg.Apps.Contains(row.Package)) _cfg.Apps.Add(row.Package);
        }
        else
        {
            _cfg.Apps.Remove(row.Package);
        }

        _store.Save();
    }

    [RelayCommand]
    private void AddIp()
    {
        string v = (NewIp ?? "").Trim();
        if (RouteRule.Classify(v, out _, out _) == RouteRule.RuleKind.Invalid)
        {
            Hint = "Введите IP (1.2.3.4) или подсеть (10.0.0.0/8)";
            return;
        }

        if (_cfg.IpRules.Any(r => string.Equals(r.Value, v, StringComparison.OrdinalIgnoreCase)))
        {
            Hint = "Такое правило уже есть";
            return;
        }

        var rule = new RouteRule { Value = v, Enabled = true };
        Attach(rule);
        _cfg.IpRules.Add(rule);
        IpRules.Add(rule);
        _store.Save();
        NewIp = "";
        Hint = $"Добавлено: {v} - идёт напрямую, мимо VPN";
    }

    [RelayCommand]
    private void RemoveIp(RouteRule? r)
    {
        if (r is null) return;
        r.PropertyChanged -= OnRulePropertyChanged;
        _cfg.IpRules.Remove(r);
        IpRules.Remove(r);
        _store.Save();
        Hint = $"Удалено: {r.Value}";
    }

    private void Attach(RouteRule r) => r.PropertyChanged += OnRulePropertyChanged;
    private void OnRulePropertyChanged(object? s, System.ComponentModel.PropertyChangedEventArgs e) => _store.Save();
}