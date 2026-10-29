using Avalonia;

namespace Chameleon.Gui;

sealed class Program
{
    private const string InstanceMutexName = @"Local\Chameleon.Gui.Instance";
    private const string ShowEventName = @"Local\Chameleon.Gui.Show";

    /// <summary>Запущено из автозапуска: окно не показываем, сразу в трей.</summary>
    public static bool StartedFromAutoStart { get; private set; }

    /// <summary>Сигнал «покажи окно» от второго запуска (ждёт первый экземпляр).</summary>
    public static EventWaitHandle? ShowSignal { get; private set; }

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => CrashLog("AppDomain", e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) => CrashLog("Task", e.Exception);

        // Один экземпляр: второй дерётся бы с первым за SOCKS-порт и TUN-адаптер.
        // Повторный запуск просто просит уже работающую копию показать окно.
        using var instance = new Mutex(initiallyOwned: true, InstanceMutexName, out bool firstInstance);
        ShowSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        if (!firstInstance)
        {
            ShowSignal.Set();
            return;
        }

        StartedFromAutoStart = args.Contains(AutoStartManager.AutoStartArg);

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            CrashLog("Main", ex);
            throw;
        }
    }

    /// <summary>Пишет только настоящие падения (без маркеров каждого запуска), файл не растёт бесконечно.</summary>
    private static void CrashLog(string where, object? ex)
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "chameleon-crash.txt");
            if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024) File.Delete(path);
            File.AppendAllText(path, $"=== {DateTime.Now:yyyy-MM-dd HH:mm:ss} {where} ===\n{ex}\n\n");
        }
        catch
        {
            // ignored
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    private static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
