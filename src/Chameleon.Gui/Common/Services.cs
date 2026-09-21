using Chameleon.Tunnel;

namespace Chameleon.Gui.Common;

/// <summary>
/// Лёгкий сервис-локатор: единый общий стейт для всех страниц (одно подключение,
/// один список профилей). Без внешних DI-пакетов.
/// </summary>
public static class Services
{
    /// <summary>Единственный оркестратор туннеля на всё приложение.</summary>
    public static TunnelService Tunnel { get; } = new();

    /// <summary>Хранилище профилей и общих настроек.</summary>
    public static ProfileStore Store { get; } = ProfileStore.Load();

    /// <summary>Единый живой лог (подписка на TunnelService.Log - ровно один раз).</summary>
    public static System.Collections.ObjectModel.ObservableCollection<string> Log { get; } = new();

    static Services()
    {
        Tunnel.Log += (_, message) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Log.Add($"{System.DateTime.Now:HH:mm:ss}  {message}");
            while (Log.Count > 1000) Log.RemoveAt(0);
        });
    }
}