namespace Chameleon.Mobile;

/// <summary>Одно установленное приложение (для маршрутизации по приложениям).</summary>
/// <param name="Icon">PNG-байты иконки (или null) - UI сам сделает Bitmap.</param>
public sealed record AppEntry(string Package, string Label, byte[]? Icon);