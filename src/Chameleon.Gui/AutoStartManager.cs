using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security;
using System.Text;

namespace Chameleon.Gui;

/// <summary>
/// Автозапуск при входе в систему (Windows). Приложение требует прав администратора
/// (TUN и маршруты), а такие программы Windows НЕ запускает из ключа реестра Run -
/// UAC молча блокирует их при входе. Поэтому автозапуск - задача Планировщика с
/// наивысшими правами: она стартует уже повышенной, без запроса UAC.
/// </summary>
public static class AutoStartManager
{
    private const string TaskName = "Chameleon";
    private const string LegacyRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Аргумент командной строки, с которым приложение запускается из автозапуска.</summary>
    public const string AutoStartArg = "--autostart";

    public static bool IsSupported => OperatingSystem.IsWindows();

    public static bool IsEnabled() => OperatingSystem.IsWindows() && Schtasks($"/Query /TN \"{TaskName}\"") == 0;

    /// <summary>Включить/выключить. Бросает при ошибке (сообщение - для UI).</summary>
    public static void Set(bool enabled)
    {
        if (!OperatingSystem.IsWindows()) return;
        RemoveLegacyRunValue();

        if (!enabled)
        {
            if (IsEnabled() && Schtasks($"/Delete /TN \"{TaskName}\" /F") != 0)
                throw new InvalidOperationException("не удалось удалить задачу автозапуска");
            return;
        }

        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("неизвестен путь к программе");
        string xmlPath = Path.Combine(Path.GetTempPath(), $"chameleon-task-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(xmlPath, BuildTaskXml(exe), Encoding.Unicode);
            if (Schtasks($"/Create /TN \"{TaskName}\" /XML \"{xmlPath}\" /F") != 0)
                throw new InvalidOperationException("не удалось создать задачу автозапуска в Планировщике");
        }
        finally
        {
            try
            {
                File.Delete(xmlPath);
            }
            catch
            {
                // ignored
            }
        }
    }

    /// <summary>
    /// XML задачи: при входе ТЕКУЩЕГО пользователя, с наивысшими правами, в его сеансе
    /// (InteractiveToken - окно и трей видны). Без лимита времени и без условий
    /// «только от сети» - иначе на ноутбуке от батареи задача не стартовала бы.
    /// </summary>
    private static string BuildTaskXml(string exe)
    {
        string user = SecurityElement.Escape($"{Environment.UserDomainName}\\{Environment.UserName}");
        string command = SecurityElement.Escape(exe);
        string workDir = SecurityElement.Escape(Path.GetDirectoryName(exe) ?? "");
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>Chameleon: запуск при входе в систему</Description></RegistrationInfo>
              <Triggers>
                <LogonTrigger><Enabled>true</Enabled><UserId>{user}</UserId><Delay>PT5S</Delay></LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{user}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <Enabled>true</Enabled>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{command}</Command>
                  <Arguments>{AutoStartArg}</Arguments>
                  <WorkingDirectory>{workDir}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    private static int Schtasks(string args)
    {
        var psi = new ProcessStartInfo("schtasks.exe", args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit(10_000);
        return p.HasExited ? p.ExitCode : -1;
    }

    /// <summary>Старый способ (ключ Run) не работал для программы с правами администратора - убираем.</summary>
    [SupportedOSPlatform("windows")]
    private static void RemoveLegacyRunValue()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(LegacyRunKey, writable: true);
            key?.DeleteValue(TaskName, throwOnMissingValue: false);
        }
        catch
        {
            // ignored
        }
    }
}
