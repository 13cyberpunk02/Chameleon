using System;
using System.IO;

namespace Chameleon.Mobile;

/// <summary>Хранит последнюю chameleon:// ссылку в файле приложения (переживает перезапуск).</summary>
public static class MobileStore
{
    private static string Path =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "chameleon.link");

    public static string LoadLink()
    {
        try
        {
            return File.Exists(Path) ? File.ReadAllText(Path).Trim() : "";
        }
        catch
        {
            return "";
        }
    }

    public static void SaveLink(string link)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(Path, link.Trim());
        }
        catch
        {
            // ignored
        }
    }
}