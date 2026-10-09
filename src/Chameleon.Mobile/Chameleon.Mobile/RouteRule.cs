using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Chameleon.Mobile;

/// <summary>Режим маршрутизации по приложениям.</summary>
public enum AppRouteMode
{
    All = 0,
    Allowed = 1,
    Disallowed = 2
}

/// <summary>
/// Правило обхода туннеля по IP/подсети: такой трафик идёт напрямую, мимо VPN.
/// Домены на мобиле не поддерживаем (ненадёжно из-за CDN).
/// </summary>
public sealed class RouteRule : INotifyPropertyChanged
{
    public string Value { get; set; } = "";

    public bool Enabled
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            OnPC();
        }
    } = true;

    public enum RuleKind
    {
        IPv4,
        Cidr,
        Invalid
    }

    [JsonIgnore] public RuleKind Kind => Classify(Value, out _, out _);

    [JsonIgnore]
    public string KindLabel => Kind switch
    {
        RuleKind.IPv4 => "IP", RuleKind.Cidr => "подсеть", _ => "неверно",
    };

    /// <summary>Разбор значения: IPv4 ("1.2.3.4") или CIDR ("10.0.0.0/8"). Для IP префикс = 32.</summary>
    public static RuleKind Classify(string value, out System.Net.IPAddress? network, out int prefix)
    {
        network = null;
        prefix = 32;
        if (string.IsNullOrWhiteSpace(value)) return RuleKind.Invalid;
        value = value.Trim();

        int slash = value.IndexOf('/');
        if (slash >= 0)
        {
            string ipPart = value[..slash];
            if (System.Net.IPAddress.TryParse(ipPart, out var net)
                && net.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                && int.TryParse(value[(slash + 1)..], out int p) && p is >= 0 and <= 32)
            {
                network = net;
                prefix = p;
                return RuleKind.Cidr;
            }

            return RuleKind.Invalid;
        }

        if (System.Net.IPAddress.TryParse(value, out var ip)
            && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            network = ip;
            prefix = 32;
            return RuleKind.IPv4;
        }

        return RuleKind.Invalid;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPC([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

/// <summary>Настройки маршрутизации: режим по приложениям + список пакетов + IP-исключения.</summary>
public sealed class RoutingConfig
{
    public AppRouteMode Mode { get; set; } = AppRouteMode.All;
    public List<string> Apps { get; set; } = [];
    public List<RouteRule> IpRules { get; set; } = [];
}