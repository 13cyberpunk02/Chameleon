using System.Collections.ObjectModel;
using Chameleon.Core.Proxy;
using Chameleon.Gui.Common;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Chameleon.Gui.ViewModels;

/// <summary>Страница «Серверы»: список профилей, выбор, добавить из ссылки, удалить.</summary>
public sealed partial class ServersViewModel : PageViewModel
{
    public override string Title => "Серверы";
    public override string Icon => "🖥";

    private readonly ProfileStore _store = Services.Store;

    public ObservableCollection<ServerProfile> Profiles { get; } = [];

    [ObservableProperty] private ServerProfile? _selected;
    [ObservableProperty] private string _hint = "";

    /// <summary>Буфер обмена задаёт View (VM не зависит от Avalonia напрямую).</summary>
    public Func<Task<string?>>? GetClipboard { get; set; }

    public Func<string, Task>? SetClipboard { get; set; }

    public ServersViewModel()
    {
        foreach (var p in _store.Profiles) Profiles.Add(p);
        Selected = _store.Selected ?? Profiles.FirstOrDefault();
    }

    partial void OnSelectedChanged(ServerProfile? value)
    {
        RemoveCommand.NotifyCanExecuteChanged();
        if (value is null) return;
        _store.SelectedIndex = Profiles.IndexOf(value);
        _store.Save();
    }

    [RelayCommand]
    private async Task AddFromLinkAsync()
    {
        string? text = GetClipboard is null ? null : await GetClipboard();
        if (!ChameleonLink.TryParse(text, out var link, out var err) || link is null)
        {
            Hint = "В буфере обмена нет ссылки chameleon:// (" + err + ")";
            return;
        }

        var profile = ServerProfile.FromLink(link);
        Profiles.Add(profile);
        _store.Profiles.Add(profile);
        Selected = profile;
        _store.SelectedIndex = Profiles.IndexOf(profile);
        _store.Save();
        Hint = $"Профиль добавлен: {profile.Display}";
    }

    [RelayCommand]
    private async Task CopyLinkAsync()
    {
        if (Selected is null)
        {
            Hint = "Выберите профиль";
            return;
        }

        if (SetClipboard is not null) await SetClipboard(Selected.ToLink());
        Hint = "Ссылка профиля скопирована в буфер обмена";
    }

    private bool CanRemove => Selected is not null;

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private void Remove()
    {
        if (Selected is null) return;
        string name = Selected.Display;
        int idx = Profiles.IndexOf(Selected);
        _store.Profiles.Remove(Selected);
        Profiles.Remove(Selected);
        Selected = Profiles.Count > 0 ? Profiles[Math.Min(idx, Profiles.Count - 1)] : null;
        _store.SelectedIndex = Selected is null ? 0 : Profiles.IndexOf(Selected);
        _store.Save();
        Hint = $"Профиль удалён: {name}";
    }
}