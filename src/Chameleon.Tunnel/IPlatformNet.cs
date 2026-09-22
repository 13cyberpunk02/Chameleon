using System.Runtime.InteropServices;

namespace Chameleon.Tunnel;

/// <summary>
/// Платформенные операции с сетью: узнать текущий шлюз, добавить/снять маршруты,
/// настроить TUN-адаптер. Windows реализован; Linux - задел (не реализовано).
/// </summary>
public interface IPlatformNet
{
    /// <summary>Текущий шлюз по умолчанию и индекс интерфейса (до поднятия TUN).</summary>
    Task<(string GatewayIp, int InterfaceIndex)> GetDefaultRouteAsync(CancellationToken ct);

    /// <summary>Маршрут-исключение: до серверного IP - мимо TUN, через реальный шлюз.</summary>
    Task AddHostRouteAsync(string destinationIp, string gatewayIp, int interfaceIndex, CancellationToken ct);

    Task RemoveHostRouteAsync(string destinationIp, CancellationToken ct);

    /// <summary>Bypass: маршрут сети (IP/CIDR) НАПРЯМУЮ через реальный шлюз (мимо TUN).</summary>
    Task AddBypassRouteAsync(string network, int prefix, string gatewayIp, int interfaceIndex, CancellationToken ct);

    Task RemoveBypassRouteAsync(string network, int prefix, CancellationToken ct);

    /// <summary>Назначить TUN-адаптеру адрес и DNS.</summary>
    Task ConfigureTunAsync(TunnelOptions options, int interfaceIndex, CancellationToken ct);

    /// <summary>Завернуть весь трафик в TUN (через 0.0.0.0/1 и 128.0.0.0/1, не трогая основной default).</summary>
    Task AddDefaultViaTunAsync(TunnelOptions options, CancellationToken ct);

    Task RemoveDefaultViaTunAsync(TunnelOptions options, CancellationToken ct);

    static IPlatformNet Create() => RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        ? new WindowsPlatformNet()
        : new LinuxPlatformNet();
}