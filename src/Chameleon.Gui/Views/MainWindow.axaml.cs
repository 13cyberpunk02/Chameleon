using System;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Chameleon.Gui.ViewModels;

namespace Chameleon.Gui.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }
    
    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            vm.GetClipboard = async () => Clipboard is null ? null : await Clipboard.TryGetTextAsync();
            vm.SetClipboard = async text => { if (Clipboard is not null) await Clipboard.SetTextAsync(text); };
            vm.PickTun2SocksFile = PickTun2SocksAsync;
        }
    }
    
    private async System.Threading.Tasks.Task<string?> PickTun2SocksAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Выберите tun2socks",
            AllowMultiple = false,
            FileTypeFilter = OperatingSystem.IsWindows()
                ? new[] { new FilePickerFileType("tun2socks") { Patterns = new[] { "*.exe" } }, FilePickerFileTypes.All }
                : new[] { FilePickerFileTypes.All },
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    protected override async void OnOpened(System.EventArgs e)
    {
        base.OnOpened(e);
        if (DataContext is MainWindowViewModel vm) await vm.TryAutoConnectAsync();
    }
}