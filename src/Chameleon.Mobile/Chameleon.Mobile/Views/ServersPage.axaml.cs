using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Chameleon.Mobile.ViewModels;

namespace Chameleon.Mobile.Views;

public partial class ServersPage : UserControl
{
    public ServersPage()
    {
        InitializeComponent();
    }

    private async void OnPasteClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ServersPageViewModel vm) return;

        string? text = null;

        try
        {
            text = Vpn.Clipboard?.GetText();
        }
        catch
        {
            // ignored
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            try
            {
                var clip = TopLevel.GetTopLevel(this)?.Clipboard;
                if (clip is not null) text = await clip.TryGetTextAsync();
            }
            catch
            {
                // ignored
            }
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            vm.Hint = "Буфер пуст (скопируйте ссылку chameleon://)";
            return;
        }

        vm.AddLink(text);
    }
}