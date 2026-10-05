using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;
using Svg.Skia;

namespace Chameleon.Gui;

/// <summary>
/// Иконка окна/трея. Рисуется ЧИСТО средствами Avalonia (RenderTargetBitmap +
/// DrawingContext), БЕЗ Skia-рендеринга SVG - потому что на net10 нативный Skia
/// (SKSvg/SKPicture.CullRect) падает с access violation (0xC0000005).
///
/// Это простая фирменная иконка (зелёный щит с «C»), хорошо читаемая в трее (16px),
/// где детальный лого всё равно превратился бы в кашу. Детальный SVG-логотип
/// показывается В ОКНЕ (sidebar). Для трея сверху - цветная точка статуса.
/// </summary>
public static class AppIcon
{
    private const int Size = 64;

    public static WindowIcon Load() => Render(null);
    public static WindowIcon ForStatus(Chameleon.Tunnel.TunnelStatus status) => Render(StatusColor(status));

    private static WindowIcon Render(Color? statusDot)
    {
        var bmp = new RenderTargetBitmap(new PixelSize(Size, Size), new Vector(96, 96));
        using (var ctx = bmp.CreateDrawingContext())
        {
            ctx.DrawRectangle(new SolidColorBrush(Color.Parse("#101826")), null,
                new RoundedRect(new Rect(0, 0, Size, Size), 14));

            var shield = BuildShield();
            var grad = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.Parse("#4AE086"), 0),
                    new GradientStop(Color.Parse("#2EA043"), 1),
                },
            };
            ctx.DrawGeometry(grad, null, shield);

            if (statusDot is not { } color) return new WindowIcon(bmp);
            double r = Size * 0.18, cx = Size - r - 3, cy = Size - r - 3;
            ctx.DrawEllipse(Brushes.White, null, new Point(cx, cy), r + 1.5, r + 1.5);
            ctx.DrawEllipse(new SolidColorBrush(color), null, new Point(cx, cy), r, r);
        }

        return new WindowIcon(bmp);
    }

    /// <summary>Геометрия щита по центру иконки.</summary>
    private static Geometry BuildShield()
    {
        var g = new StreamGeometry();
        using var c = g.Open();
        c.BeginFigure(new Point(32, 10), true);
        c.LineTo(new Point(52, 18));
        c.LineTo(new Point(52, 34));
        c.CubicBezierTo(new Point(52, 46), new Point(44, 52), new Point(32, 56));
        c.CubicBezierTo(new Point(20, 52), new Point(12, 46), new Point(12, 34));
        c.LineTo(new Point(12, 18));
        c.EndFigure(true);

        return g;
    }

    private static Color StatusColor(Tunnel.TunnelStatus s) => s switch
    {
        Tunnel.TunnelStatus.Connected => Color.Parse("#3FB950"),
        Tunnel.TunnelStatus.Connecting => Color.Parse("#D29922"),
        Tunnel.TunnelStatus.Reconnecting => Color.Parse("#D29922"),
        Tunnel.TunnelStatus.Error => Color.Parse("#F85149"),
        _ => Color.Parse("#8B949E"),
    };
}