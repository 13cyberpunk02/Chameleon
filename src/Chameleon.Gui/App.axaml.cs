using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Chameleon.Gui.Common;
using Chameleon.Gui.ViewModels;
using Chameleon.Gui.Views;

namespace Chameleon.Gui;

public partial class App : Application
{
    private MainWindow? _window;
    private TrayIcon? _tray;
    private bool _exiting;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _window = new MainWindow { DataContext = new MainWindowViewModel(), Icon = AppIcon.Load() };
            desktop.MainWindow = _window;
            _window.Closing += (_, e) =>
            {
                if (!_exiting)
                {
                    e.Cancel = true;
                    _window!.Hide();
                }
            };
            SetupTray(desktop);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void SetupTray(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var menu = new NativeMenu();
        var open = new NativeMenuItem("Открыть");
        open.Click += (_, _) => ShowWindow();
        var exit = new NativeMenuItem("Выход");
        exit.Click += (_, _) =>
        {
            _exiting = true;
            _ = Services.Tunnel.DisconnectAsync();
            desktop.Shutdown();
        };
        menu.Items.Add(open);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(exit);

        _tray = new TrayIcon
        {
            Icon = AppIcon.ForStatus(Services.Tunnel.Status),
            ToolTipText = "Chameleon - отключено",
            Menu = menu,
            IsVisible = true,
        };
        _tray.Clicked += (_, _) => ShowWindow();
        TrayIcon.SetIcons(this, [_tray]);

        Services.Tunnel.StatusChanged += (_, status) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (_tray is null) return;
                _tray.Icon = AppIcon.ForStatus(status);
                _tray.ToolTipText = "Chameleon - " + StatusText(status);
            });
    }

    private static string StatusText(Tunnel.TunnelStatus s) => s switch
    {
        Tunnel.TunnelStatus.Connected => "подключено",
        Tunnel.TunnelStatus.Connecting => "подключение…",
        Tunnel.TunnelStatus.Reconnecting => "переподключение…",
        Tunnel.TunnelStatus.Error => "ошибка",
        _ => "отключено",
    };

    private void ShowWindow()
    {
        if (_window is null) return;
        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
    }
}