using System.Collections.Concurrent;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Chameleon.Gui.Common;

/// <summary>
/// Код страны («fi») → картинка флага из ресурсов (assets/flags, общие с мобильной
/// версией). Картинки, а не эмодзи: в Windows флаги-эмодзи не рисуются - вместо них «FI».
/// Неизвестный/пустой код → null (разметка показывает вместо флага значок глобуса).
/// </summary>
public sealed class FlagConverter : IValueConverter
{
    public static readonly FlagConverter Instance = new();

    private static readonly ConcurrentDictionary<string, Bitmap?> Cache = new();

    public static Bitmap? Load(string? code)
    {
        if (code is not { Length: 2 }) return null;
        return Cache.GetOrAdd(code.ToLowerInvariant(), c =>
        {
            try
            {
                var uri = new Uri($"avares://Chameleon.Gui/Assets/Flags/{c}.png");
                return AssetLoader.Exists(uri) ? new Bitmap(AssetLoader.Open(uri)) : null;
            }
            catch
            {
                return null;
            }
        });
    }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Load(value as string);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
