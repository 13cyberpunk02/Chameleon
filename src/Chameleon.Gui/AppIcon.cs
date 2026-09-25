using System.Reflection;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using SkiaSharp;
using Svg.Skia;

namespace Chameleon.Gui;

/// <summary>
/// Иконка приложения/трея из ВЕКТОРНОГО логотипа (SVG), рендерится через Skia.
/// Для трея добавляется маленькая цветная точка статуса в углу
/// (серый/жёлтый/зелёный/красный), чтобы состояние было видно из трея.
/// </summary>
public static class AppIcon
{
    private const int IconSize = 64;

    private static string LoadSvg()
    {
        var asm = Assembly.GetExecutingAssembly();
        string name = Array.Find(asm.GetManifestResourceNames(),
                          n => n.EndsWith("chameleon-shield.svg", StringComparison.OrdinalIgnoreCase))
                      ?? throw new InvalidOperationException("Ресурс логотипа не найден");
        using var s = asm.GetManifestResourceStream(name)!;
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    private static readonly Lazy<SKPicture?> Picture = new(() =>
    {
        try
        {
            using var svg = new SKSvg();
            using var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(LoadSvg()));
            svg.Load(ms);
            return svg.Picture;
        }
        catch
        {
            return null;
        }
    });

    /// <summary>Иконка окна (без индикатора статуса).</summary>
    public static WindowIcon Load() => Render(null);

    /// <summary>Иконка трея с точкой статуса.</summary>
    public static WindowIcon ForStatus(Tunnel.TunnelStatus status) => Render(StatusColor(status));

    private static WindowIcon Render(SKColor? statusDot)
    {
        var info = new SKImageInfo(IconSize, IconSize, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        var pic = Picture.Value;
        if (pic is not null)
        {
            var r = pic.CullRect;
            float scale = IconSize / Math.Max(r.Width, r.Height);
            canvas.Save();
            canvas.Scale(scale);
            canvas.Translate(-r.Left, -r.Top);
            canvas.DrawPicture(pic);
            canvas.Restore();
        }
        
        if (statusDot is { } color)
        {
            float rad = IconSize * 0.18f;
            float cx = IconSize - rad - 2, cy = IconSize - rad - 2;
            using var border = new SKPaint { Color = SKColors.White, IsAntialias = true };
            using var fill = new SKPaint { Color = color, IsAntialias = true };
            canvas.DrawCircle(cx, cy, rad + 1.5f, border);
            canvas.DrawCircle(cx, cy, rad, fill);
        }

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return new WindowIcon(new Bitmap(new MemoryStream(data.ToArray())));
    }

    private static SKColor StatusColor(Chameleon.Tunnel.TunnelStatus s) => s switch
    {
        Chameleon.Tunnel.TunnelStatus.Connected => new SKColor(0x3F, 0xB9, 0x50),
        Chameleon.Tunnel.TunnelStatus.Connecting => new SKColor(0xD2, 0x99, 0x22), 
        Chameleon.Tunnel.TunnelStatus.Reconnecting => new SKColor(0xD2, 0x99, 0x22),
        Chameleon.Tunnel.TunnelStatus.Error => new SKColor(0xF8, 0x51, 0x49),
        _ => new SKColor(0x8B, 0x94, 0x9E),
    };
}