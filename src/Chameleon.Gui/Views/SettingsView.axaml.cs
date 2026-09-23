using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Chameleon.Gui.ViewModels;

namespace Chameleon.Gui.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (DataContext is SettingsViewModel vm)
        {
            vm.PickTun2SocksFile = PickAsync;
            var top = TopLevel.GetTopLevel(this);
            vm.SetClipboard = async text =>
            {
                if (top?.Clipboard is not null) await top.Clipboard.SetTextAsync(text);
            };
        }
    }

    private async System.Threading.Tasks.Task<string?> PickAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return null;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Выберите tun2socks",
            AllowMultiple = false,
            FileTypeFilter = OperatingSystem.IsWindows()
                ? new[]
                {
                    new FilePickerFileType("tun2socks") { Patterns = ["*.exe"] }, FilePickerFileTypes.All
                }
                : new[] { FilePickerFileTypes.All },
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }
}