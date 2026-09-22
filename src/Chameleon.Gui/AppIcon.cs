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
        byte[] bytes = System.Convert.FromBase64String(PngBase64);
        return new WindowIcon(new Bitmap(new System.IO.MemoryStream(bytes)));
    }

    private const string PngDisconnected =
        "iVBORw0KGgoAAAANSUhEUgAAACAAAAAgCAYAAABzenr0AAAAfklEQVR42u3XsRGAMAwDQE1CQZWZqKkZhzo1S8IEYDvgs7hToVpfxQqmuZ2VgQC/AyzrZiYF4CkegSCjOAJBdrmFQLR874eZCCIE8JQ/IdyAt+URhAswUn6HMAFflnsQAggggAB8LyHFLSi/hhR7oHwRUWxCilVM8y/Q1ywrF9Pv11J4zqLzAAAAAElFTkSuQmCC";

    private const string PngConnected =
        "iVBORw0KGgoAAAANSUhEUgAAACAAAAAgCAYAAABzenr0AAAAgElEQVR42u3XsRGAMAwDQE1CQZWOLViGLZgkg7ELTAC2Az6LOxWq9VWsYJrbWRkI8DvA0lczKQBP8QgEGcURCLLLLQSi5duxm4kgQgBP+RPCDXhbHkG4ACPldwgT8GW5ByGAAAIIwPcSUtyC8mtIsQfKFxHFJqRYxTT/An3NsnIBMyCo346XDb4AAAAASUVORK5CYII=";

    private const string PngConnecting =
        "iVBORw0KGgoAAAANSUhEUgAAACAAAAAgCAYAAABzenr0AAAAfElEQVR42u3XsRGAMAwDQE+SgoqZGIyZqFkomQAsB3xW7lSo1lexYm3be2VMgOUA19ncpACQ4hmIZRRHIJZd7iEsWt7vw00EEQIg5W8IGPC1PIKAADPlTwgX8Gc5ghBAAAEE4HsJKW5B+TWk2APli4hiE1KsYpp/gb5mWRmTmXQU+6iKWwAAAABJRU5ErkJggg==";

    private const string PngError =
        "iVBORw0KGgoAAAANSUhEUgAAACAAAAAgCAYAAABzenr0AAAAe0lEQVR42u3XsRHAIAwDQE+SIlUmYTCmy1ZkAmKLxGdxp0K1vsLCjvMalTEBtgPcrblJAUSKVyCWUYxALLvcQxhaPnp3gyAgQKT8DREGfC1HECHASvkM4QL+LI8gBBBAAAH4XkKKW1B+DSn2QPkiotiEFKuY5l+gr1lWHqToI0zmYlpXAAAAAElFTkSuQmCC";

    private static WindowIcon FromBase64(string b64)
        => new WindowIcon(new Bitmap(new System.IO.MemoryStream(System.Convert.FromBase64String(b64))));

    /// <summary>Иконка трея по статусу подключения (серый/жёлтый/зелёный/красный).</summary>
    public static WindowIcon ForStatus(Chameleon.Tunnel.TunnelStatus status) => status switch
    {
        Chameleon.Tunnel.TunnelStatus.Connected => FromBase64(PngConnected),
        Chameleon.Tunnel.TunnelStatus.Connecting => FromBase64(PngConnecting),
        Chameleon.Tunnel.TunnelStatus.Reconnecting => FromBase64(PngConnecting),
        Chameleon.Tunnel.TunnelStatus.Error => FromBase64(PngError),
        _ => FromBase64(PngDisconnected),
    };
}