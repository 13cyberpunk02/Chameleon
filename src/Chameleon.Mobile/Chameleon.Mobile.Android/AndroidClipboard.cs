using Android.Content;

namespace Chameleon.Mobile.Android;

/// <summary>Нативное чтение буфера через Android ClipboardManager (надёжнее, чем
/// Avalonia-клипборд, который на Android возвращает пусто).</summary>
public sealed class AndroidClipboard : IClipboard
{
    public string? GetText()
    {
        try
        {
            var ctx = global::Android.App.Application.Context;
            var cm = (ClipboardManager?)ctx.GetSystemService(Context.ClipboardService);
            var clip = cm?.PrimaryClip;
            if (clip is null || clip.ItemCount == 0) return null;
            var item = clip.GetItemAt(0);
            return item?.CoerceToText(ctx)?.ToString();
        }
        catch { return null; }
    }
}