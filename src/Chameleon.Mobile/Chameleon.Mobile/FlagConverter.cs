using System;
using System.Collections.Concurrent;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Chameleon.Mobile;

/// <summary>
/// Код страны («fi») → картинка флага из ресурсов (assets/flags, общие с десктопом).
/// Картинки, а не эмодзи: так флаг одинаково выглядит на всех версиях Android и в десктопе.
/// Неизвестный/пустой код → null (разметка показывает значок глобуса).
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
                var uri = new Uri($"avares://Chameleon.Mobile/Assets/Flags/{c}.png");
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
