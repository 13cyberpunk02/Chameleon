using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Chameleon.Gui.Common;
using Chameleon.Gui.ViewModels;
using Chameleon.Gui.Views;

namespace Chameleon.Gui;

public partial class App : Application
{
    private MainWindow? _window;
    private TrayIcon? _tray;
    private NativeMenuItem? _exitItem;
    private bool _exiting;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Окно закрывается в трей, поэтому приложение живёт, пока его явно не завершат.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var vm = new MainWindowViewModel();
            _window = new MainWindow { DataContext = vm, Icon = AppIcon.Load() };
            _window.Closing += (_, e) =>
            {
                if (!_exiting)
                {
                    e.Cancel = true;
                    _window!.Hide();
                }
            };

            // Завершение «снаружи» (выход из Windows и т.п.) - тоже через корректное отключение.
            desktop.ShutdownRequested += (_, e) =>
            {
                if (_exiting) return;
                e.Cancel = true;
                _ = ExitAsync(desktop);
            };

            SetupTray(desktop);
            ListenForShowRequests();

            // Из автозапуска - тихо в трей; обычный запуск - с окном.
            if (!Program.StartedFromAutoStart) desktop.MainWindow = _window;

            if (Services.Store.AutoConnect) vm.Connect.ConnectOnStartup();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void SetupTray(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var menu = new NativeMenu();
        var open = new NativeMenuItem("Открыть");
        open.Click += (_, _) => ShowWindow();
        _exitItem = new NativeMenuItem("Выход");
        _exitItem.Click += (_, _) => _ = ExitAsync(desktop);
        menu.Items.Add(open);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(_exitItem);

        _tray = new TrayIcon
        {
            Icon = AppIcon.ForStatus(Services.Tunnel.Status),
            ToolTipText = "Chameleon - " + StatusText(Services.Tunnel.Status),
            Menu = menu,
            IsVisible = true,
        };
        _tray.Clicked += (_, _) => ShowWindow();
        TrayIcon.SetIcons(this, [_tray]);

        Services.Tunnel.StatusChanged += (_, status) =>
            Dispatcher.UIThread.Post(() =>
            {
                if (_tray is null) return;
                _tray.Icon = AppIcon.ForStatus(status);
                _tray.ToolTipText = "Chameleon - " + StatusText(status);
            });
    }

    /// <summary>
    /// Выход. Сначала ДОЖИДАЕМСЯ отключения туннеля: tun2socks - отдельный процесс и сам
    /// не умрёт, а маршрут по умолчанию через TUN без него оставил бы систему без
    /// интернета. Раньше отключение запускалось и тут же вызывался Shutdown.
    /// </summary>
    private async Task ExitAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (_exiting) return;
        _exiting = true;
        if (_exitItem is not null)
        {
            _exitItem.Header = "Отключение…";
            _exitItem.IsEnabled = false;
        }

        try
        {
            await Task.WhenAny(Services.Tunnel.DisconnectAsync(), Task.Delay(TimeSpan.FromSeconds(15)));
        }
        catch
        {
            // ignored
        }

        if (_tray is not null) _tray.IsVisible = false;
        desktop.Shutdown();
    }

    /// <summary>Повторный запуск программы сигналит сюда - показываем окно уже работающей копии.</summary>
    private void ListenForShowRequests()
    {
        if (Program.ShowSignal is not { } signal) return;
        var thread = new Thread(() =>
        {
            while (true)
            {
                signal.WaitOne();
                Dispatcher.UIThread.Post(ShowWindow);
            }
        }) { IsBackground = true, Name = "show-signal" };
        thread.Start();
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
