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

        var tray = new TrayIcon { Icon = AppIcon.Load(), ToolTipText = "Chameleon", Menu = menu, IsVisible = true };
        tray.Clicked += (_, _) => ShowWindow();
        TrayIcon.SetIcons(this, new TrayIcons { tray });
    }

    private void ShowWindow()
    {
        if (_window is null) return;
        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
    }
}