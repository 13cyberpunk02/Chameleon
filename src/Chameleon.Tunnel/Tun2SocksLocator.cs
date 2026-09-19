namespace Chameleon.Tunnel;

/// <summary>Ищет бинарник tun2socks в типичных местах, чтобы не вводить путь руками.</summary>
public static class Tun2SocksLocator
{
    /// <summary>Возможные имена бинарника.</summary>
    private static string[] Names => OperatingSystem.IsWindows()
        ? ["tun2socks.exe", "tun2socks-windows-amd64.exe", "tun2socks-windows.exe"]
        : ["tun2socks", "tun2socks-linux-amd64"];

    /// <summary>
    /// Возвращает путь к tun2socks, если найден рядом с приложением, в ./tools или в PATH.
    /// null - не найден (тогда пользователь укажет вручную).
    /// </summary>
    public static string? Find()
    {
        string baseDir = AppContext.BaseDirectory;
        var dirs = new List<string>
        {
            baseDir,
            Path.Combine(baseDir, "tools"),
            Directory.GetCurrentDirectory(),
            Path.Combine(Directory.GetCurrentDirectory(), "tools"),
        };
        dirs.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        foreach (string dir in dirs.Distinct())
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            foreach (string name in Names)
            {
                string candidate = Path.Combine(dir, name);
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
        }
        return null;
    }

    /// <summary>Есть ли рядом с tun2socks файл wintun.dll (нужен на Windows).</summary>
    public static bool HasWintunNextTo(string tun2SocksPath)
    {
        if (!OperatingSystem.IsWindows()) return true;
        string? dir = Path.GetDirectoryName(Path.GetFullPath(tun2SocksPath));
        return dir is not null && File.Exists(Path.Combine(dir, "wintun.dll"));
    }
}