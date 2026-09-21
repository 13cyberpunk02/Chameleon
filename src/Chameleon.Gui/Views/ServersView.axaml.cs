using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Chameleon.Gui.ViewModels;

namespace Chameleon.Gui.Views;

public partial class ServersView : UserControl
{
    public ServersView() => InitializeComponent();

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (DataContext is ServersViewModel vm)
        {
            var top = TopLevel.GetTopLevel(this);
            vm.GetClipboard = async () => top?.Clipboard is null ? null : await top.Clipboard.TryGetTextAsync();
            vm.SetClipboard = async text =>
            {
                if (top?.Clipboard is not null) await top.Clipboard.SetTextAsync(text);
            };
        }
    }
}