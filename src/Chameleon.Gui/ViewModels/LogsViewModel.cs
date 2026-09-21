using System.Collections.ObjectModel;
using Chameleon.Gui.Common;
using CommunityToolkit.Mvvm.Input;

namespace Chameleon.Gui.ViewModels;

/// <summary>Страница «Логи»: живой лог туннеля, копирование и очистка.</summary>
public sealed partial class LogsViewModel : PageViewModel
{
    public override string Title => "Логи";
    public override string Icon => "📋";

    public ObservableCollection<string> Log => Services.Log;

    /// <summary>Буфер обмена задаёт View.</summary>
    public Func<string, Task>? SetClipboard { get; set; }

    [RelayCommand]
    private async Task CopyAsync()
    {
        if (SetClipboard is not null)
            await SetClipboard(string.Join(Environment.NewLine, Log));
    }

    [RelayCommand]
    private void Clear() => Log.Clear();
}