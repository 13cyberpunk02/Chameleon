using System.Net;
using System.Text.Json;

namespace Chameleon.Core.Proxy;

/// <summary>Внешний IP и страна, под которыми трафик выходит в интернет (то есть где стоит сервер).</summary>
/// <param name="CountryCode">ISO 3166-1 alpha-2 в нижнем регистре («fi») или null, если страну узнать не удалось.</param>
/// <param name="CountryName">Название страны (по-русски, если сервис умеет) или null.</param>
public sealed record ExitInfo(string Ip, string? CountryCode, string? CountryName);

/// <summary>
/// Узнаёт IP и страну выхода, обращаясь к гео-сервису ЧЕРЕЗ туннель (локальный SOCKS5).
/// Сервис видит только IP сервера - то же, что раньше видел api.ipify.org, нового
/// раскрытия нет. Сервисы перебираются по очереди: если один недоступен или
/// заблокирован, пробуется следующий; последний отдаёт только IP, без страны.
/// </summary>
public static class ExitGeo
{
    public static async Task<ExitInfo?> LookupAsync(string socksHostPort, TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        using var handler = new SocketsHttpHandler
        {
            Proxy = new WebProxy($"socks5://{socksHostPort}"),
            UseProxy = true,
        };
        using var http = new HttpClient(handler) { Timeout = timeout };

        foreach (var provider in Providers)
        {
            try
            {
                string json = await http.GetStringAsync(provider.Url, cancellationToken).ConfigureAwait(false);
                if (provider.Parse(json) is { } info) return info;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // следующий сервис
            }
        }

        return null;
    }

    private sealed record Provider(string Url, Func<string, ExitInfo?> Parse);

    private static readonly Provider[] Providers =
    [
        new("https://ipwho.is/?lang=ru&fields=ip,success,country,country_code", json =>
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.False) return null;
            return Make(Str(r, "ip"), Str(r, "country_code"), Str(r, "country"));
        }),
        new("https://ipinfo.io/json", json =>
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            return Make(Str(r, "ip"), Str(r, "country"), null);
        }),
        new("https://api.ipify.org?format=json", json =>
        {
            using var doc = JsonDocument.Parse(json);
            return Make(Str(doc.RootElement, "ip"), null, null);
        }),
    ];

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static ExitInfo? Make(string? ip, string? code, string? name)
    {
        if (string.IsNullOrWhiteSpace(ip)) return null;
        code = code is { Length: 2 } && char.IsAsciiLetter(code[0]) && char.IsAsciiLetter(code[1])
            ? code.ToLowerInvariant()
            : null;
        return new ExitInfo(ip.Trim(), code, string.IsNullOrWhiteSpace(name) ? null : name.Trim());
    }
}
