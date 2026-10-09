using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Chameleon.Mobile.ViewModels;

public partial class SettingsPageViewModel(MobileProfiles store) : ViewModelBase
{
    [ObservableProperty] private bool _autoConnect = store.AutoConnect;
    [ObservableProperty] private string _hint = "";
    [ObservableProperty] private bool _confirmClear;

    public string Version => "Chameleon Mobile 1.0";
    public string ActiveServerName => store.Active?.Name ?? "Не выбран";
    public int ServerCount => store.Profiles.Count;
    public string ClearButtonText => ConfirmClear ? "Нажмите ещё раз - удалить всё" : "Удалить все серверы";

    /// <summary>Вызывается после удаления всех профилей (MainViewModel обновит другие экраны).</summary>
    public Action? ProfilesCleared { get; set; }

    /// <summary>Обновить показания при открытии вкладки (активный сервер, счётчик, тумблер).</summary>
    public void Refresh()
    {
        if (_autoConnect != store.AutoConnect) AutoConnect = store.AutoConnect;
        ConfirmClear = false;
        OnPropertyChanged(nameof(ActiveServerName));
        OnPropertyChanged(nameof(ServerCount));
    }

    partial void OnAutoConnectChanged(bool value)
    {
        store.AutoConnect = value;
        store.Save();
        Hint = value ? "Авто-подключение включено" : "Авто-подключение выключено";
    }

    partial void OnConfirmClearChanged(bool value) => OnPropertyChanged(nameof(ClearButtonText));

    [RelayCommand]
    private void ClearProfiles()
    {
        if (ServerCount == 0)
        {
            Hint = "Список серверов уже пуст";
            return;
        }

        if (!ConfirmClear)
        {
            ConfirmClear = true;
            Hint = "Это удалит все серверы. Нажмите ещё раз для подтверждения.";
            return;
        }

        store.Profiles.Clear();
        store.ActiveIndex = -1;
        store.Save();
        ConfirmClear = false;
        ProfilesCleared?.Invoke();
        OnPropertyChanged(nameof(ActiveServerName));
        OnPropertyChanged(nameof(ServerCount));
        Hint = "Все серверы удалены";
    }
}