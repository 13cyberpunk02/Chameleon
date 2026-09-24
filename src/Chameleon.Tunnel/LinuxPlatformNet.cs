using System.Net.NetworkInformation;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace Chameleon.Tunnel;

/// <summary>
/// Реализация под Linux через iproute2 (`ip`). Требует root. НЕ проверено в
/// песочнице - тестировать на реальном Linux и при необходимости подправить
/// (особенно DNS: на разных дистрибутивах управляется по-разному).
///
/// На Linux маршруты/адреса задаются по ИМЕНИ устройства, а не по индексу:
///  - TUN-адаптеру назначаем адрес и поднимаем его (options.TunDeviceName);
///  - маршрут по умолчанию заворачиваем в TUN через `dev`;
///  - исключения (host/bypass) идут `via` реальный шлюз (kernel сам выберет dev).
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxPlatformNet : IPlatformNet
{
    public Action<string>? Log { get; set; }

    private Task Ip(string args, CancellationToken ct) => ProcessRunner.RunAsync("ip", args, Log, ct);

    public async Task<(string GatewayIp, int InterfaceIndex)> GetDefaultRouteAsync(CancellationToken ct)
    {
        string output = await ProcessRunner.RunCaptureAsync("ip", "route show default", ct).ConfigureAwait(false);
        var m = Regex.Match(output, @"default\s+via\s+(\d+\.\d+\.\d+\.\d+)\s+dev\s+(\S+)");
        if (!m.Success)
            throw new InvalidOperationException("Не найден маршрут по умолчанию (ip route show default)");

        string gateway = m.Groups[1].Value;
        string dev = m.Groups[2].Value;
        int idx = IndexByName(dev);
        Log?.Invoke($"default-маршрут: шлюз {gateway}, интерфейс {dev} (if {idx})");
        return (gateway, idx);
    }

    public Task AddHostRouteAsync(string destinationIp, string gatewayIp, int interfaceIndex, CancellationToken ct)
        => Ip($"route add {destinationIp}/32 via {gatewayIp}", ct);

    public Task RemoveHostRouteAsync(string destinationIp, CancellationToken ct)
        => Ip($"route del {destinationIp}/32", ct);

    public Task AddBypassRouteAsync(string network, int prefix, string gatewayIp, int interfaceIndex,
        CancellationToken ct)
        => Ip($"route add {network}/{prefix} via {gatewayIp}", ct);

    public Task RemoveBypassRouteAsync(string network, int prefix, CancellationToken ct)
        => Ip($"route del {network}/{prefix}", ct);

    public async Task ConfigureTunAsync(TunnelOptions o, int interfaceIndex, CancellationToken ct)
    {
        await Ip($"addr add {o.TunAddress}/{o.TunPrefix} dev {o.TunDeviceName}", ct).ConfigureAwait(false);
        await Ip($"link set dev {o.TunDeviceName} up", ct).ConfigureAwait(false);
        
        try
        {
            await ProcessRunner.RunAsync("resolvectl", $"dns {o.TunDeviceName} {o.TunDns}", Log, ct)
                .ConfigureAwait(false);
            await ProcessRunner.RunAsync("resolvectl", $"default-route {o.TunDeviceName} true", Log, ct)
                .ConfigureAwait(false);
        }
        catch
        {
            Log?.Invoke("resolvectl недоступен - DNS не настроен автоматически. " +
                        "Если имена не резолвятся, пропишите DNS вручную (/etc/resolv.conf) или используйте DoH.");
        }
    }

    public async Task AddDefaultViaTunAsync(TunnelOptions o, CancellationToken ct)
    {
        await Ip($"route add 0.0.0.0/1 dev {o.TunDeviceName}", ct).ConfigureAwait(false);
        await Ip($"route add 128.0.0.0/1 dev {o.TunDeviceName}", ct).ConfigureAwait(false);
        Log?.Invoke($"маршрут по умолчанию заведён через TUN ({o.TunDeviceName})");
    }

    public async Task RemoveDefaultViaTunAsync(TunnelOptions o, CancellationToken ct)
    {
        await Ip("route del 0.0.0.0/1", ct).ConfigureAwait(false);
        await Ip("route del 128.0.0.0/1", ct).ConfigureAwait(false);
    }

    private static int IndexByName(string dev)
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            if (ni.Name == dev)
            {
                try
                {
                    return ni.GetIPProperties().GetIPv4Properties().Index;
                }
                catch
                {
                    return 0;
                }
            }

        return 0;
    }
}