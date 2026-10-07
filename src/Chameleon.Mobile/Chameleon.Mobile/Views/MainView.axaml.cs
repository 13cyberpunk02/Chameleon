using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Chameleon.Mobile.ViewModels;

namespace Chameleon.Mobile.Views;

public partial class MainView : UserControl
{
    public MainView()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (DataContext is not MainViewModel vm) return;
        var top = TopLevel.GetTopLevel(this);
        vm.GetClipboard = async () => top?.Clipboard is null ? null : await top.Clipboard.TryGetTextAsync();
    }
}