using System;
namespace Chameleon.Mobile;

/// <summary>
/// Глобальный доступ UI к VPN + статус. Android-сервис сообщает статус через Report,
/// UI подписывается на StatusChanged.
/// </summary>
public static class Vpn
{
    public static IVpnController? Current { get; set; }

    public static VpnStatus Status { get; private set; } = VpnStatus.Disconnected;
    public static event Action<VpnStatus>? StatusChanged;

    /// <summary>Сервис/код сообщает новый статус (потокобезопасно для UI через Avalonia Dispatcher).</summary>
    public static void Report(VpnStatus status)
    {
        Status = status;
        var h = StatusChanged;
        if (h is null) return;
        Avalonia.Threading.Dispatcher.UIThread.Post(() => h(status));
    }
}