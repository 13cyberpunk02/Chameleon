using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Chameleon.Core.Proxy;

namespace Chameleon.Mobile.ViewModels;

public partial class ServersPageViewModel : ViewModelBase
{
    private readonly MobileProfiles _store;

    public ObservableCollection<MobileProfile> Profiles { get; } = [];
    [ObservableProperty] private MobileProfile? _selected;
    [ObservableProperty] private string _hint = "";
    
    [ObservableProperty] private string _fName = "";
    [ObservableProperty] private string _fHost = "";
    [ObservableProperty] private string _fPort = "443";
    [ObservableProperty] private string _fKey = "";
    [ObservableProperty] private string _fSni = "";
    [ObservableProperty] private string _fClientKey = "";
    [ObservableProperty] private string _fExtra = "";

    [ObservableProperty] private bool _formExpanded;
    [ObservableProperty] private bool _isEditing;
    private MobileProfile? _editing;

    public string FormHeaderText => IsEditing ? "Редактирование сервера" : "Добавить сервер вручную";
    public string FormPrimaryText => IsEditing ? "Сохранить" : "Добавить";
    public string FormSecondaryText => IsEditing ? "Отмена" : "Очистить";

    partial void OnIsEditingChanged(bool value)
    {
        OnPropertyChanged(nameof(FormHeaderText));
        OnPropertyChanged(nameof(FormPrimaryText));
        OnPropertyChanged(nameof(FormSecondaryText));
    }

    public Action? ActiveChanged { get; set; }

    public ServersPageViewModel(MobileProfiles store)
    {
        _store = store;
        foreach (var p in _store.Profiles) Profiles.Add(p);
        if (_store.ActiveIndex >= 0 && _store.ActiveIndex < Profiles.Count)
            Selected = Profiles[_store.ActiveIndex];
        RefreshActiveFlags();
    }

    public bool IsActive(MobileProfile p) => _store.Active == p;

    /// <summary>Перечитать список из хранилища (например, после «Удалить все» в настройках).</summary>
    public void Reload()
    {
        Profiles.Clear();
        foreach (var p in _store.Profiles) Profiles.Add(p);
        Selected = (_store.ActiveIndex >= 0 && _store.ActiveIndex < Profiles.Count)
            ? Profiles[_store.ActiveIndex]
            : null;
        _editing = null;
        IsEditing = false;
        FormExpanded = false;
        RefreshActiveFlags();
    }
    
    [RelayCommand]
    private void BeginEdit(MobileProfile? p)
    {
        if (p is null) return;
        if (!ChameleonLink.TryParse(p.Link, out var l, out _) || l is null)
        {
            Hint = "Не удалось разобрать ссылку этого сервера";
            return;
        }

        FName = l.Name ?? "";
        FHost = l.Host;
        FPort = l.Port.ToString();
        FKey = l.ServerPublicKeyHex;
        FSni = l.Sni;
        FClientKey = l.ClientPrivateKeyHex ?? "";
        FExtra = string.Join(", ", l.ExtraCarriers);

        _editing = p;
        IsEditing = true;
        FormExpanded = true;
        Hint = $"Редактирование: {p.Name}";
    }

    
    [RelayCommand]
    private void AddManual()
    {
        string host = (FHost ?? "").Trim();
        if (host.Length == 0)
        {
            Hint = "Укажите адрес сервера";
            return;
        }

        if (!int.TryParse((FPort ?? "").Trim(), out int port) || port is < 1 or > 65535)
        {
            Hint = "Неверный порт (1–65535)";
            return;
        }

        string key = (FKey ?? "").Trim().ToLowerInvariant();
        if (key.Length != 64 || !IsHex(key))
        {
            Hint = "Ключ сервера - ровно 64 hex-символа";
            return;
        }

        string ck = (FClientKey ?? "").Trim();
        if (ck.Length > 0 && (ck.Length != 64 || !IsHex(ck)))
        {
            Hint = "Ключ клиента - ровно 64 hex-символа";
            return;
        }

        string sni = (FSni ?? "").Trim();
        if (sni.Length == 0) sni = host;

        string name = (FName ?? "").Trim();
        if (name.Length == 0) name = host;

        var extras = new List<string>();
        foreach (var part in (FExtra ?? "").Split(',',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            extras.Add(part);

        string link = new ChameleonLink(
            host, port, key, sni, extras, name,
            ck.Length > 0 ? ck.ToLowerInvariant() : null).Build();

        if (IsEditing && _editing is not null)
        {
            _editing.Name = name;
            _editing.Link = link;
            _store.Save();
            if (ReferenceEquals(_store.Active, _editing)) ActiveChanged?.Invoke();
            Hint = $"Сохранено: {name}";
            ClearForm();
            return;
        }

        AddLink(link);
        if (Hint.StartsWith("Добавлен")) ClearForm();
    }

    [RelayCommand]
    private void ClearForm()
    {
        bool wasEditing = IsEditing;
        FName = "";
        FHost = "";
        FPort = "443";
        FKey = "";
        FSni = "";
        FClientKey = "";
        FExtra = "";
        _editing = null;
        IsEditing = false;
        if (wasEditing) FormExpanded = false;
    }

    /// <summary>Добавить профиль из готовой ссылки (из буфера или собранной из полей).</summary>
    public void AddLink(string? link)
    {
        link = link?.Trim() ?? "";
        if (!ChameleonLink.TryParse(link, out var parsed, out string? err) || parsed is null)
        {
            Hint = "Некорректная ссылка chameleon://" + (err is null ? "" : $" ({err})");
            return;
        }

        var prof = new MobileProfile { Name = parsed.Name ?? parsed.Host, Link = link };
        _store.Profiles.Add(prof);
        Profiles.Add(prof);
        if (_store.ActiveIndex < 0) SetActive(prof);
        _store.Save();
        RefreshActiveFlags();
        Hint = $"Добавлен: {prof.Name}";
    }

    [RelayCommand]
    private void SetActive(MobileProfile? p)
    {
        if (p is null) return;
        int i = _store.Profiles.IndexOf(p);
        if (i < 0) return;
        _store.ActiveIndex = i;
        _store.Save();
        Selected = p;
        RefreshActiveFlags();
        ActiveChanged?.Invoke();
        Hint = $"Активный сервер: {p.Name}";
    }

    [RelayCommand]
    private void Remove(MobileProfile? p)
    {
        if (p is null) return;
        int i = _store.Profiles.IndexOf(p);
        _store.Profiles.Remove(p);
        Profiles.Remove(p);
        if (i == _store.ActiveIndex) _store.ActiveIndex = _store.Profiles.Count > 0 ? 0 : -1;
        else if (i < _store.ActiveIndex) _store.ActiveIndex--;
        _store.Save();
        RefreshActiveFlags();
        ActiveChanged?.Invoke();
        Hint = "Удалён";
    }

    private void RefreshActiveFlags()
    {
        var active = _store.Active;
        foreach (var p in Profiles) p.IsActive = ReferenceEquals(p, active);
    }

    private static bool IsHex(string s) =>
         s.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F'));
}