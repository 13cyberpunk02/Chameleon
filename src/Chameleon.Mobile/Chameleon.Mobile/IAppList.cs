namespace Chameleon.Mobile;

/// <summary>Список установленных приложений (реализуется в Android через PackageManager).</summary>
public interface IAppList
{
    System.Collections.Generic.IReadOnlyList<AppEntry> GetInstalled();
}