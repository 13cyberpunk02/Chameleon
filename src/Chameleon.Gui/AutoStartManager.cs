using System.Runtime.Versioning;

namespace Chameleon.Gui;

/// <summary>Управление автозапуском приложения при входе в систему (Windows: реестр HKCU\Run).</summary>
public static class AutoStartManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Chameleon";

    public static bool IsSupported => OperatingSystem.IsWindows();

    public static bool IsEnabled() => IsSupported && IsEnabledWindows();

    public static void Set(bool enabled)
    {
        if (!IsSupported) return;
        SetWindows(enabled);
    }

    [SupportedOSPlatform("windows")]
    private static bool IsEnabledWindows()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is not null;
    }

    [SupportedOSPlatform("windows")]
    private static void SetWindows(bool enabled)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
        if (key is null) return;
        if (enabled)
        {
            string exe = Environment.ProcessPath ?? "";
            if (!string.IsNullOrEmpty(exe)) key.SetValue(ValueName, $"\"{exe}\"");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}