using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Chameleon.Gui.ViewModels;
using Chameleon.Gui.Views;

namespace Chameleon.Gui;

public partial class App : Application
{
    private MainWindow? _window;
    private MainWindowViewModel? _vm;
    private TrayIcon? _tray;
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _vm = new MainWindowViewModel();
            _window = new MainWindow { DataContext = _vm, Icon = AppIcon.Load() };
            desktop.MainWindow = _window;

            _window.Closing += (_, e) =>
            {
                if (_exiting) return;
                e.Cancel = true; _window!.Hide();
            };

            SetupTray(desktop);
        }
        base.OnFrameworkInitializationCompleted();
    }
    
    private bool _exiting;

    private void SetupTray(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var menu = new NativeMenu();

        var open = new NativeMenuItem("Открыть");
        open.Click += (_, _) => ShowWindow();

        var connect = new NativeMenuItem("Подключить");
        connect.Click += (_, _) => { if (_vm?.ConnectCommand.CanExecute(null) == true) _vm.ConnectCommand.Execute(null); };

        var disconnect = new NativeMenuItem("Отключить");
        disconnect.Click += (_, _) => { if (_vm?.DisconnectCommand.CanExecute(null) == true) _vm.DisconnectCommand.Execute(null); };

        var exit = new NativeMenuItem("Выход");
        exit.Click += (_, _) => { _exiting = true; _ = _vm?.DisconnectCommand.ExecuteAsync(null); desktop.Shutdown(); };

        menu.Items.Add(open);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(connect);
        menu.Items.Add(disconnect);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(exit);

        _tray = new TrayIcon { Icon = AppIcon.Load(), ToolTipText = "Chameleon", Menu = menu, IsVisible = true };
        _tray.Clicked += (_, _) => ShowWindow(); 

        TrayIcon.SetIcons(this, [_tray]);
    }

    private void ShowWindow()
    {
        if (_window is null) return;
        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
    }
}