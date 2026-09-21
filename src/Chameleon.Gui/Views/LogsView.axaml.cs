using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Chameleon.Gui.ViewModels;

namespace Chameleon.Gui.Views;

public partial class LogsView : UserControl
{
    public LogsView() => InitializeComponent();

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (DataContext is not LogsViewModel vm) return;

        var top = TopLevel.GetTopLevel(this);
        vm.SetClipboard = async text =>
        {
            if (top?.Clipboard is not null) await top.Clipboard.SetTextAsync(text);
        };

        vm.Log.CollectionChanged += OnLogChanged;
        ScrollToEnd();
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        base.OnUnloaded(e);
        if (DataContext is LogsViewModel vm) vm.Log.CollectionChanged -= OnLogChanged;
    }

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e) => ScrollToEnd();

    private void ScrollToEnd()
    {
        Dispatcher.UIThread.Post(() => { this.FindControl<ScrollViewer>("LogScroll")?.ScrollToEnd(); },
            DispatcherPriority.Background);
    }
}