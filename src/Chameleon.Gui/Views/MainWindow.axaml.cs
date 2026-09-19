using Avalonia.Controls;
using Avalonia.Input.Platform;
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
        }
    }
}