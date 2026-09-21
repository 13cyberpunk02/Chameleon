using Avalonia.Controls;
using Avalonia.Media.Imaging;

namespace Chameleon.Gui;

/// <summary>Иконка приложения/трея, вшитая в код (base64 PNG) - без внешнего файла.</summary>
public static class AppIcon
{
    private const string PngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAACAAAAAgCAYAAABzenr0AAAAf0lEQVR42u3XsRGAMAwDQE3CBmxATc04GYLBshVMALZDfBZ3KlTrq1jBsq9XZSDA7wDbeZhJAXiKRyDIKI5AkF1uIRAtb72ZiSBCAE/5G8IN+FoeQbgAI+VPCBMws9yDEEAAAQTgewkpbkH5NaTYA+WLiGITUqximn+BvmZZuQEwBOyt0vslJAAAAABJRU5ErkJggg==";

    public static WindowIcon Load()
    {
        var bytes = Convert.FromBase64String(PngBase64);
        return new WindowIcon(new Bitmap(new MemoryStream(bytes)));
    }
}