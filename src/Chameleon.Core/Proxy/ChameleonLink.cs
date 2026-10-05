using System.Text;

namespace Chameleon.Core.Proxy;

/// <summary>
/// Конфиг-ссылка для быстрой передачи настроек клиенту:
///   chameleon://host:port?key=HEX&amp;sni=DOMAIN[&amp;extra=h:p,h:p]#Имя
/// Пользователь копирует одну строку - клиент заполняет поля сам.
/// </summary>
public sealed record ChameleonLink(
    string Host,
    int Port,
    string ServerPublicKeyHex,
    string Sni,
    IReadOnlyList<string> ExtraCarriers,
    string? Name,
    string? ClientPrivateKeyHex = null) 
{
    public const string Scheme = "chameleon://";

    /// <summary>Собирает ссылку из полей.</summary>
    public string Build()
    {
        var sb = new StringBuilder(Scheme);
        sb.Append(Host).Append(':').Append(Port);
        sb.Append("?key=").Append(ServerPublicKeyHex.ToLowerInvariant());
        sb.Append("&sni=").Append(Uri.EscapeDataString(Sni));
        if (ExtraCarriers.Count > 0)
            sb.Append("&extra=").Append(Uri.EscapeDataString(string.Join(",", ExtraCarriers)));
        if (!string.IsNullOrWhiteSpace(ClientPrivateKeyHex))
            sb.Append("&ck=").Append(ClientPrivateKeyHex.ToLowerInvariant());
        if (!string.IsNullOrWhiteSpace(Name))
            sb.Append('#').Append(Uri.EscapeDataString(Name));
        return sb.ToString();
    }

    public override string ToString() => Build();

    /// <summary>Разбирает ссылку. Бросает <see cref="FormatException"/> при неверном формате.</summary>
    public static ChameleonLink Parse(string link) =>
        !TryParse(link, out var result, out var error) ? throw new FormatException(error) : result!;

    public static bool TryParse(string? link, out ChameleonLink? result, out string? error)
    {
        result = null;
        error = null;
        if (string.IsNullOrWhiteSpace(link))
        {
            error = "Пустая ссылка";
            return false;
        }

        link = link.Trim();
        if (!link.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase))
        {
            error = $"Ссылка должна начинаться с {Scheme}";
            return false;
        }

        var rest = link[Scheme.Length..];

        string? name = null;
        var hash = rest.IndexOf('#');
        if (hash >= 0)
        {
            name = Uri.UnescapeDataString(rest[(hash + 1)..]);
            rest = rest[..hash];
        }

        var authority = rest;
        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var q = rest.IndexOf('?');
        if (q >= 0)
        {
            authority = rest[..q];
            foreach (var pair in rest[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                if (eq < 0) continue;
                query[pair[..eq]] = Uri.UnescapeDataString(pair[(eq + 1)..]);
            }
        }

        var colon = authority.LastIndexOf(':');
        if (colon <= 0)
        {
            error = "Нет host:port";
            return false;
        }

        var host = authority[..colon];
        if (!int.TryParse(authority[(colon + 1)..], out int port) || port is < 1 or > 65535)
        {
            error = "Неверный порт";
            return false;
        }

        if (!query.TryGetValue("key", out var key) || string.IsNullOrWhiteSpace(key))
        {
            error = "Нет ключа (key=)";
            return false;
        }

        key = key.Trim();
        if (!IsHex(key) || key.Length != 64)
        {
            error = "Ключ должен быть 64 hex-символа (32 байта)";
            return false;
        }

        query.TryGetValue("sni", out var sni);
        if (string.IsNullOrWhiteSpace(sni)) sni = host;

        var extras = new List<string>();
        if (query.TryGetValue("extra", out var extra) && !string.IsNullOrWhiteSpace(extra))
            extras.AddRange(extra.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        string? ck = null;
        if (query.TryGetValue("ck", out var ckv) && !string.IsNullOrWhiteSpace(ckv))
        {
            ckv = ckv.Trim();
            if (!IsHex(ckv) || ckv.Length != 64)
            {
                error = "Ключ клиента (ck) должен быть 64 hex-символа";
                return false;
            }

            ck = ckv.ToLowerInvariant();
        }

        result = new ChameleonLink(host, port, key.ToLowerInvariant(), sni, extras, name, ck);
        return true;
    }

    private static bool IsHex(string s) =>
         s.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F'));
}