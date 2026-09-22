using System.Collections.ObjectModel;
using Chameleon.Gui.Common;
using Chameleon.Tunnel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Chameleon.Gui.ViewModels;

/// <summary>Страница «Маршрутизация» (split-tunnel): адреса, идущие мимо туннеля.</summary>
public sealed partial class RoutingViewModel : PageViewModel
{
    public override string Title => "Маршрутизация";
    public override string Icon => "🔀";

    private readonly ProfileStore _store = Services.Store;

    public ObservableCollection<BypassRule> Rules { get; } = new();

    [ObservableProperty] private string _newValue = "";
    [ObservableProperty] private string _newNote = "";
    [ObservableProperty] private BypassRule? _selected;
    [ObservableProperty] private string _hint = "";

    public RoutingViewModel()
    {
        foreach (var r in _store.BypassRules) Rules.Add(r);
    }

    [RelayCommand]
    private void Add()
    {
        string value = NewValue.Trim();
        if (BypassRule.Classify(value, out _, out _) == BypassRule.RuleKind.Invalid)
        {
            Hint = "Введите IP (1.2.3.4), подсеть (10.0.0.0/8) или домен (ssh.example.com)";
            return;
        }

        var rule = new BypassRule
            { Value = value, Enabled = true, Note = string.IsNullOrWhiteSpace(NewNote) ? null : NewNote.Trim() };
        Rules.Add(rule);
        _store.BypassRules.Add(rule);
        _store.Save();
        NewValue = "";
        NewNote = "";
        Hint = $"Добавлено: {rule.Value} ({rule.KindLabel}) - идёт напрямую мимо туннеля";
    }

    private bool CanRemove => Selected is not null;

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private void Remove()
    {
        if (Selected is null) return;
        string v = Selected.Value;
        _store.BypassRules.Remove(Selected);
        Rules.Remove(Selected);
        _store.Save();
        Hint = $"Удалено: {v}";
    }

    /// <summary>Вкл/выкл правило (вызывается из UI). Применится при следующем подключении.</summary>
    public void Toggle(BypassRule rule)
    {
        _store.Save();
        Hint = "Изменения применятся при следующем подключении";
    }

    partial void OnSelectedChanged(BypassRule? value) => RemoveCommand.NotifyCanExecuteChanged();
}