using System.Net;
using System.Net.Sockets;

namespace Chameleon.Tunnel;

/// <summary>
/// Одно правило обхода туннеля (bypass): трафик к этому адресу идёт НАПРЯМУЮ,
/// мимо TUN (через реальный шлюз). Полезно для SSH-серверов, банков, локальной
/// сети - чтобы они не рвались при переподключении VPN и не шли через VPS.
///
/// Тип задаётся строкой: IP ("1.2.3.4"), подсеть CIDR ("10.0.0.0/8") или домен
/// ("ssh.example.com" - резолвится в IP при подключении).
/// </summary>
public sealed class BypassRule
{
    public string Value { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string? Note { get; set; }

    public enum RuleKind
    {
        IPv4,
        Cidr,
        Domain,
        Invalid
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public RuleKind Kind => Classify(Value, out _, out _);

    [System.Text.Json.Serialization.JsonIgnore]
    public string KindLabel => Kind switch
    {
        RuleKind.IPv4 => "IP",
        RuleKind.Cidr => "подсеть",
        RuleKind.Domain => "домен",
        _ => "неверно",
    };

    /// <summary>Разбирает значение правила на тип. Для CIDR отдаёт сеть и префикс.</summary>
    public static RuleKind Classify(string value, out IPAddress? network, out int prefix)
    {
        network = null;
        prefix = 32;
        if (string.IsNullOrWhiteSpace(value)) return RuleKind.Invalid;
        value = value.Trim();

        int slash = value.IndexOf('/');
        if (slash >= 0)
        {
            string ipPart = value[..slash];
            if (IPAddress.TryParse(ipPart, out var net) && net.AddressFamily == AddressFamily.InterNetwork
                                                        && int.TryParse(value[(slash + 1)..], out int p) &&
                                                        p is >= 0 and <= 32)
            {
                network = net;
                prefix = p;
                return RuleKind.Cidr;
            }

            return RuleKind.Invalid;
        }

        if (IPAddress.TryParse(value, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork)
        {
            network = ip;
            prefix = 32;
            return RuleKind.IPv4;
        }

        // Похоже на домен: есть точка и ХОТЯ БЫ ОДНА буква (иначе это битый IP,
        // напр. "256.1.1.1"), нет пробелов, допустимые символы.
        if (value.Contains('.') && value.Any(char.IsLetter) && !value.Contains(' ') && value.All(c =>
                char.IsLetterOrDigit(c) || c is '.' or '-' or '_'))
            return RuleKind.Domain;

        return RuleKind.Invalid;
    }

    /// <summary>
    /// Возвращает список (сеть, префикс) для добавления маршрутов-исключений.
    /// Домены резолвятся в IPv4-адреса. Пустой список - правило невалидно/не резолвится.
    /// </summary>
    public async Task<IReadOnlyList<(IPAddress Network, int Prefix)>> ResolveAsync(CancellationToken ct)
    {
        var kind = Classify(Value, out var net, out int prefix);
        switch (kind)
        {
            case RuleKind.IPv4:
            case RuleKind.Cidr:
                return net is null ? [] : [(net, prefix)];
            case RuleKind.Domain:
                try
                {
                    var addrs = await Dns.GetHostAddressesAsync(Value.Trim(), ct).ConfigureAwait(false);
                    return addrs.Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                        .Select(a => (a, 32)).ToList();
                }
                catch
                {
                    return [];
                }
            default:
                return [];
        }
    }
}