using System;

namespace Chameleon.Mobile;

/// <summary>Глобальный доступ UI к VPN: команды, статус, статистика.</summary>
public static class Vpn
{
    public static IVpnController? Current { get; set; }
    public static IClipboard? Clipboard { get; set; }

    public static VpnStatus Status { get; private set; } = VpnStatus.Disconnected;
    public static event Action<VpnStatus>? StatusChanged;

    public static VpnStats Stats { get; private set; }
    public static event Action<VpnStats>? StatsChanged;

    public static string PublicIp { get; private set; } = "-";
    public static event Action<string>? IpChanged;

    public static void Report(VpnStatus status)
    {
        Status = status;
        Post(() => StatusChanged?.Invoke(status));
    }

    public static void ReportStats(VpnStats stats)
    {
        Stats = stats;
        Post(() => StatsChanged?.Invoke(stats));
    }

    public static void ReportIp(string ip)
    {
        PublicIp = ip;
        Post(() => IpChanged?.Invoke(ip));
    }

    private static void Post(Action a)
    {
        try
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(a);
        }
        catch
        {
            // ignored
        }
    }
}