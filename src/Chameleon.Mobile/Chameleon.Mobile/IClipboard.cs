namespace Chameleon.Mobile;

/// <summary>Чтение системного буфера обмена (реализуется в Android через ClipboardManager,
/// т.к. Avalonia-клипборд на Android отдаёт пусто).</summary>
public interface IClipboard
{
    string? GetText();
}