using Avalonia;

namespace Chameleon.Gui;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        void Dump(string where, object? ex) =>
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(AppContext.BaseDirectory, "chameleon-crash.txt"),
                $"=== {where} ===\n{ex}\n\n");

        AppDomain.CurrentDomain.UnhandledException += (_, e) => Dump("AppDomain", e.ExceptionObject);
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) => Dump("Task", e.Exception);

        try
        {
            Dump("start", "запуск BuildAvaloniaApp");   // маркер, что дошли сюда
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            Dump("exit", "нормальный выход");
        }
        catch (Exception ex)
        {
            Dump("Main", ex);
            throw;
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