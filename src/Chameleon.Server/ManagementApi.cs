using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Chameleon.Core.Crypto;
using Chameleon.Core.Proxy;

namespace Chameleon.Server;

/// <summary>
/// Лёгкий management REST API (на HttpListener - без ASP.NET, тот же маленький
/// образ). Отдаёт статистику/сессии/события и управляет allowlist'ом клиентов.
/// Предназначен для внутренней сети / за nginx с TLS.
///
/// Вход: скрипты шлют Bearer-токен, панель обменивает токен на сессию в HttpOnly-cookie
/// (скрипт на странице её не прочитает, а SameSite=Strict закрывает CSRF). Панель и API
/// живут на одном origin, поэтому CORS не нужен и намеренно не включается.
/// После серии неверных токенов источник получает 429 на минуту.
/// </summary>
public sealed class ManagementApi : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly ChameleonServer _server;
    private readonly ServerEventLog _events;
    private readonly SqliteStore _store;
    private readonly byte[] _token;
    private readonly bool _trustProxy;
    private readonly ConcurrentDictionary<string, long> _panelSessions = new(); // id -> когда истекает (TickCount64)
    private readonly ConcurrentDictionary<string, (int Count, long Until)> _failures = new();
    private readonly string _serverPublicKey;
    private readonly string _sni;
    private readonly string _publicAddr;
    private readonly bool _ownCertificate;
    private readonly bool _hasDecoy;
    private readonly DateTime _startedUtc = DateTime.UtcNow;
    private readonly CancellationTokenSource _cts = new();
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private const string SessionCookie = "chameleon_session";
    private const int SessionLifetimeSeconds = 12 * 3600;
    private const int MaxFailures = 10;
    private const int FailureWindowMs = 60_000;
    private const int FailedLoginDelayMs = 500;
    private const int MaxClientNameLength = 100;
    private const string DefaultSni = "www.example-cdn.com";

    // Серия коротких сессий почти без трафика: обычно обрывы или блокировка соединения.
    private static readonly TimeSpan ShortSessionWindow = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ShortSessionMaxDuration = TimeSpan.FromSeconds(60);
    private const long ShortSessionMaxBytes = 64 * 1024;
    private const int ShortSessionMinCount = 3;

    public ManagementApi(string prefix, string token, ChameleonServer server, ServerEventLog events,
        SqliteStore store, string serverPublicKey, string sni, string publicAddr, bool ownCertificate, bool hasDecoy,
        bool trustProxy = false)
    {
        _ownCertificate = ownCertificate;
        _hasDecoy = hasDecoy;
        _server = server;
        _events = events;
        _store = store;
        _token = Encoding.UTF8.GetBytes(token);
        _trustProxy = trustProxy;
        _serverPublicKey = serverPublicKey;
        _sni = sni;
        _publicAddr = publicAddr;
        _listener.Prefixes.Add(prefix);
    }

    public void Start()
    {
        _listener.Start();
        _ = Task.Run(LoopAsync);
    }

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch
            {
                break;
            }

            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        try
        {
            var source = SourceOf(req);
            if (IsLockedOut(source))
            {
                await Write(res, 429, new { error = "too many attempts" });
                return;
            }

            var path = req.Url?.AbsolutePath.TrimEnd('/') ?? "";
            var method = req.HttpMethod;

            if ((method, path) == ("POST", "/api/login"))
            {
                await LoginAsync(req, res, source);
                return;
            }

            var auth = req.Headers["Authorization"];
            if (auth is not null)
            {
                if (!auth.StartsWith("Bearer ", StringComparison.Ordinal) || !TokenMatches(auth["Bearer ".Length..]))
                {
                    RegisterFailure(source);
                    await Write(res, 401, new { error = "unauthorized" });
                    return;
                }
            }
            // Просроченная cookie - не подбор токена: открытая вкладка опрашивает API каждые
            // несколько секунд и не должна блокировать источник.
            else if (!HasValidSession(req))
            {
                await Write(res, 401, new { error = "unauthorized" });
                return;
            }

            switch ((method, path))
            {
                case ("POST", "/api/logout"):
                    if (req.Cookies[SessionCookie]?.Value is { } sessionId) _panelSessions.TryRemove(sessionId, out _);
                    res.AddHeader("Set-Cookie", BuildCookie("", 0, req));
                    await Write(res, 200, new { ok = true });
                    break;

                case ("GET", "/api/server"):
                    await Write(res, 200, new
                    {
                        publicKey = _serverPublicKey, sni = _sni,
                        allowlist = _store.Enforced,
                        uptimeSeconds = (long)(DateTime.UtcNow - _startedUtc).TotalSeconds,
                    });
                    break;

                case ("GET", "/api/health"):
                    await Write(res, 200, Health());
                    break;

                case ("GET", "/api/stats"):
                    await Write(res, 200, _server.Stats());
                    break;

                case ("GET", "/api/sessions"):
                    await Write(res, 200, _server.ActiveSessions());
                    break;

                case ("GET", "/api/events"):
                    var n = int.TryParse(req.QueryString["n"], out var v) ? Math.Clamp(v, 1, 2000) : 100;
                    await Write(res, 200, _events.Recent(n));
                    break;

                case ("GET", "/api/clients"):
                    await Write(res, 200, ClientsWithTraffic());
                    break;

                case ("GET", "/api/traffic"):
                    await Write(res, 200, TrafficRows());
                    break;

                case ("GET", "/api/history/server"):
                {
                    var d = int.TryParse(req.QueryString["days"], out var dd) ? dd : 30;
                    await Write(res, 200,
                        _store.ServerHistory(d).Select(h => new { day = h.Day, down = h.Down, up = h.Up }));
                    break;
                }

                case ("GET", var p) when p.StartsWith("/api/history/client/"):
                {
                    var key = p["/api/history/client/".Length..];
                    var d = int.TryParse(req.QueryString["days"], out var dd) ? dd : 30;
                    await Write(res, 200,
                        _store.ClientHistory(key, d).Select(h => new { day = h.Day, down = h.Down, up = h.Up }));
                    break;
                }

                case ("POST", var p) when p.EndsWith("/kick") && p.StartsWith("/api/clients/"):
                {
                    var key = p["/api/clients/".Length..^"/kick".Length];
                    var c = _server.CloseSessionsForClient(key, "отключён вручную из панели");
                    await Write(res, 200, new { ok = true, closed = c });
                    break;
                }

                case ("POST", "/api/clients/generate"):
                {
                    var body = await ReadJson<ClientCreate>(req);
                    var name = body?.Name?.Trim() ?? "";
                    
                    var kp = X25519.GenerateKeyPair();
                    var pub = Convert.ToHexString(kp.Public).ToLowerInvariant();
                    var priv = Convert.ToHexString(kp.Private).ToLowerInvariant();
                    _store.Add(new ClientAccount { PublicKeyHex = pub, Name = name });

                    var i = _publicAddr.LastIndexOf(':');
                    var host = i > 0 ? _publicAddr[..i] : _publicAddr;
                    var port = i > 0 && int.TryParse(_publicAddr[(i + 1)..], out var pp) ? pp : 443;
                    var link = new ChameleonLink(host, port, _serverPublicKey, _sni,
                        [], string.IsNullOrEmpty(name) ? null : name, priv).Build();

                    _events.Add("created", "-", $"создан клиент {pub[..16]}… ({name})");
                    await Write(res, 200, new { publicKey = pub, privateKey = priv, name, link });
                    break;
                }

                case ("POST", "/api/clients"):
                {
                    var body = await ReadJson<ClientCreate>(req);
                    if (body?.PublicKey is null || !IsPublicKeyHex(body.PublicKey.Trim()))
                    {
                        await Write(res, 400, new { error = "publicKey must be 64 hex characters" });
                        break;
                    }

                    _store.Add(new ClientAccount { PublicKeyHex = body.PublicKey.Trim(), Name = body.Name ?? "" });
                    await Write(res, 200, new { ok = true });
                    break;
                }

                case ("PATCH", var p) when p.EndsWith("/limit") && p.StartsWith("/api/clients/"):
                {
                    var key = p["/api/clients/".Length..^"/limit".Length];
                    var body = await ReadJson<LimitPatch>(req);
                    if (body is null)
                    {
                        await Write(res, 400, new { error = "bad body" });
                        break;
                    }

                    var ok = _store.SetLimit(key, body.LimitBytes);
                    await Write(res, ok ? 200 : 404, new { ok });
                    break;
                }

                case ("PATCH", var p) when p.EndsWith("/name") && p.StartsWith("/api/clients/"):
                {
                    var key = p["/api/clients/".Length..^"/name".Length];
                    var body = await ReadJson<NamePatch>(req);
                    var name = body?.Name?.Trim();
                    if (name is null || name.Length > MaxClientNameLength)
                    {
                        await Write(res, 400, new { error = "bad name" });
                        break;
                    }

                    var ok = _store.SetName(key, name);
                    await Write(res, ok ? 200 : 404, new { ok });
                    break;
                }

                case ("PATCH", var p) when p.StartsWith("/api/clients/"):
                {
                    var key = p["/api/clients/".Length..];
                    var body = await ReadJson<ClientPatch>(req);
                    if (body is null)
                    {
                        await Write(res, 400, new { error = "bad body" });
                        break;
                    }

                    var ok = _store.SetEnabled(key, body.Enabled);
                    var closed = ok && !body.Enabled
                        ? _server.CloseSessionsForClient(key, "выключен из панели")
                        : 0;
                    await Write(res, ok ? 200 : 404, new { ok, closed });
                    break;
                }

                case ("DELETE", var p) when p.StartsWith("/api/clients/"):
                {
                    var key = p["/api/clients/".Length..];
                    var ok = _store.Remove(key);
                    var closed = ok ? _server.CloseSessionsForClient(key, "удалён из панели") : 0;
                    await Write(res, ok ? 200 : 404, new { ok, closed });
                    break;
                }

                default:
                    await Write(res, 404, new { error = "not found" });
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"management API: {req.HttpMethod} {req.Url?.AbsolutePath}: {ex}");
            try
            {
                await Write(res, 500, new { error = "internal error" });
            }
            catch
            {
                // ignored
            }
        }
    }
    
    /// <summary>Что стоит проверить в настройке и работе сервера: подсказки для дашборда и счётчики за час.</summary>
    private object Health()
    {
        var warnings = new List<object>();
        void Warn(string code, string text) => warnings.Add(new { code, text });

        if (!_ownCertificate)
            Warn("self-signed", "Самоподписанный сертификат: при проверке он выдаёт, что это не обычный сайт. " +
                                "Задайте CHAMELEON_CERT_PEM и CHAMELEON_KEY_PEM или CHAMELEON_CERT_PFX.");
        if (!_hasDecoy)
            Warn("no-decoy", "Нет сайта-прикрытия: зонд увидит заглушку вместо настоящего сайта. Задайте CHAMELEON_DECOY.");
        if (_sni == DefaultSni)
            Warn("default-sni", "Используется домен-пример в CHAMELEON_SNI: задайте настоящий домен.");
        if (!_store.Enforced)
            Warn("no-allowlist", "Allowlist выключен: подключиться может любой, кто знает ключ сервера. " +
                                 "Задайте CHAMELEON_ALLOWLIST=1.");

        var since = DateTime.UtcNow - ShortSessionWindow;
        var names = _store.Rows().ToDictionary(r => r.Key, r => r.Name);
        foreach (var group in _server.RecentClosedSessions()
                     .Where(s => s.ClosedUtc >= since && s.Duration <= ShortSessionMaxDuration &&
                                 s.BytesToClient + s.BytesFromClient <= ShortSessionMaxBytes)
                     .GroupBy(s => s.ClientKey))
        {
            if (group.Count() < ShortSessionMinCount) continue;
            var name = names.GetValueOrDefault(group.Key) is { Length: > 0 } n ? n : group.Key[..12] + "…";
            Warn("short-sessions", $"Клиент «{name}»: {group.Count()} коротких сессий почти без трафика за " +
                                   $"{ShortSessionWindow.TotalMinutes:0} минут. Похоже на обрывы или блокировку соединения.");
        }

        var hourAgo = DateTime.UtcNow.AddHours(-1);
        var recent = _events.Recent(2000).Where(e => e.TimeUtc >= hourAgo).ToList();
        return new
        {
            warnings,
            probesLastHour = recent.Count(e => e.Event == "probe"),
            deniedLastHour = recent.Count(e => e.Event == "denied"),
        };
    }

    private async Task LoginAsync(HttpListenerRequest req, HttpListenerResponse res, string source)
    {
        var body = await ReadJson<LoginRequest>(req);
        if (body?.Token is null || !TokenMatches(body.Token))
        {
            RegisterFailure(source);
            _events.Add("login-fail", source, "неверный токен панели");
            await Task.Delay(FailedLoginDelayMs).ConfigureAwait(false);
            await Write(res, 401, new { error = "unauthorized" });
            return;
        }

        _failures.TryRemove(source, out _);
        foreach (var (id, expires) in _panelSessions)
            if (Environment.TickCount64 >= expires)
                _panelSessions.TryRemove(id, out _);

        var sessionId = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        _panelSessions[sessionId] = Environment.TickCount64 + SessionLifetimeSeconds * 1000L;
        res.AddHeader("Set-Cookie", BuildCookie(sessionId, SessionLifetimeSeconds, req));
        await Write(res, 200, new { ok = true });
    }

    private bool TokenMatches(string candidate) =>
        CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(candidate)),
            SHA256.HashData(_token));

    private bool HasValidSession(HttpListenerRequest req) =>
        req.Cookies[SessionCookie]?.Value is { } id &&
        _panelSessions.TryGetValue(id, out var expires) &&
        Environment.TickCount64 < expires;

    private string BuildCookie(string value, int maxAgeSeconds, HttpListenerRequest req)
    {
        bool https = req.IsSecureConnection ||
                     (_trustProxy && req.Headers["X-Forwarded-Proto"] == "https");
        return $"{SessionCookie}={value}; Path=/; Max-Age={maxAgeSeconds}; HttpOnly; SameSite=Strict" +
               (https ? "; Secure" : "");
    }

    /// <summary>За своим nginx все запросы приходят с его адреса, настоящий источник - в X-Real-IP.
    /// Без доверия к прокси заголовку верить нельзя: его подставит сам подбирающий.</summary>
    private string SourceOf(HttpListenerRequest req) =>
        _trustProxy && req.Headers["X-Real-IP"] is { Length: > 0 } ip ? ip : req.RemoteEndPoint.Address.ToString();

    private bool IsLockedOut(string source) =>
        _failures.TryGetValue(source, out var f) && f.Count >= MaxFailures &&
        Environment.TickCount64 < f.Until;

    private void RegisterFailure(string source)
    {
        long now = Environment.TickCount64;
        _failures.AddOrUpdate(source, _ => (1, now + FailureWindowMs),
            (_, f) => now >= f.Until ? (1, now + FailureWindowMs) : (f.Count + 1, f.Until));
        foreach (var (key, f) in _failures)
            if (now >= f.Until)
                _failures.TryRemove(key, out _);
    }

    private static bool IsPublicKeyHex(string key) => key.Length == X25519.KeySize * 2 && key.All(char.IsAsciiHexDigit);

    private (Dictionary<string, long> down, Dictionary<string, long> up) LiveByClient()
    {
        var down = new Dictionary<string, long>();
        var up = new Dictionary<string, long>();
        foreach (var s in _server.ActiveSessions())
        {
            var k = s.ClientKey.ToLowerInvariant();
            down[k] = (down.GetValueOrDefault(k, 0)) + s.BytesToClient;
            up[k] = (up.GetValueOrDefault(k, 0)) + s.BytesFromClient;
        }

        return (down, up);
    }
    
    private object ClientsWithTraffic()
    {
        var (liveDown, liveUp) = LiveByClient();
        return _store.Rows().Select(r =>
        {
            var k = r.Key;
            var ld = liveDown.GetValueOrDefault(k, 0);
            var lu = liveUp.GetValueOrDefault(k, 0);
            var online = liveDown.ContainsKey(k) || liveUp.ContainsKey(k);
            var periodDown = r.PeriodDown + ld;
            var overLimit = r.LimitBytes > 0 && periodDown >= r.LimitBytes;
            var remaining = r.LimitBytes > 0 ? Math.Max(0, r.LimitBytes - periodDown) : (long?)null;
            return new
            {
                publicKeyHex = r.Key, name = r.Name, enabled = r.Enabled, addedUtc = r.AddedUtc,
                totalDown = r.TotalDown + ld, totalUp = r.TotalUp + lu,
                periodDown, periodUp = r.PeriodUp + lu,
                limitBytes = r.LimitBytes, remaining, overLimit,
                online, lastSeenUtc = online ? DateTime.UtcNow : r.LastSeenUtc,
                periodStart = r.PeriodStart,
            };
        }).ToList();
    }

    private object TrafficRows() => ClientsWithTraffic();

    private static async Task<T?> ReadJson<T>(HttpListenerRequest req)
    {
        using var reader = new StreamReader(req.InputStream, req.ContentEncoding);
        var body = await reader.ReadToEndAsync().ConfigureAwait(false);
        try
        {
            return JsonSerializer.Deserialize<T>(body, Json);
        }
        catch
        {
            return default;
        }
    }

    private static async Task Write(HttpListenerResponse res, int status, object payload)
    {
        res.StatusCode = status;
        res.ContentType = "application/json; charset=utf-8";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, Json);
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        res.Close();
    }

    private sealed record ClientCreate(string PublicKey, string? Name);

    private sealed record LoginRequest(string? Token);

    private sealed record NamePatch(string? Name);

    private sealed record ClientPatch(bool Enabled);

    private sealed record LimitPatch(long LimitBytes);

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch
        {
            // ignored
        }

        _cts.Dispose();
    }
}