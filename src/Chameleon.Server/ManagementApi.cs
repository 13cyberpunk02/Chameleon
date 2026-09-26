using System.Net;
using System.Text.Json;
using Chameleon.Core.Proxy;

namespace Chameleon.Server;

/// <summary>
/// Лёгкий management REST API (на HttpListener - без ASP.NET, тот же маленький
/// образ). Отдаёт статистику/сессии/события и управляет allowlist'ом клиентов.
/// Защищён Bearer-токеном. Предназначен для внутренней сети / за nginx с TLS.
/// </summary>
public sealed class ManagementApi : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly ChameleonServer _server;
    private readonly ServerEventLog _events;
    private readonly IClientStore _clients;
    private readonly TrafficStore _traffic;
    private readonly string _token;
    private readonly string _serverPublicKey;
    private readonly string _sni;
    private readonly DateTime _startedUtc = DateTime.UtcNow;
    private readonly CancellationTokenSource _cts = new();
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public ManagementApi(string prefix, string token, ChameleonServer server, ServerEventLog events,
        IClientStore clients, TrafficStore traffic, string serverPublicKey, string sni)
    {
        _server = server;
        _events = events;
        _clients = clients;
        _traffic = traffic;
        _token = token;
        _serverPublicKey = serverPublicKey;
        _sni = sni;
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
            res.AddHeader("Access-Control-Allow-Origin", "*");
            res.AddHeader("Access-Control-Allow-Methods", "GET, POST, DELETE, PATCH, OPTIONS");
            res.AddHeader("Access-Control-Allow-Headers", "Authorization, Content-Type");
            if (req.HttpMethod == "OPTIONS")
            {
                res.StatusCode = 204;
                res.Close();
                return;
            }

            string auth = req.Headers["Authorization"] ?? "";
            if (auth != $"Bearer {_token}")
            {
                await Write(res, 401, new { error = "unauthorized" });
                return;
            }

            string path = req.Url?.AbsolutePath.TrimEnd('/') ?? "";
            string method = req.HttpMethod;

            switch ((method, path))
            {
                case ("GET", "/api/server"):
                    await Write(res, 200, new
                    {
                        publicKey = _serverPublicKey, sni = _sni,
                        allowlist = _clients.Enforced,
                        uptimeSeconds = (long)(DateTime.UtcNow - _startedUtc).TotalSeconds,
                    });
                    break;

                case ("GET", "/api/stats"):
                    await Write(res, 200, _server.Stats());
                    break;

                case ("GET", "/api/sessions"):
                    await Write(res, 200, _server.ActiveSessions());
                    break;

                case ("GET", "/api/events"):
                    int n = int.TryParse(req.QueryString["n"], out int v) ? Math.Clamp(v, 1, 2000) : 100;
                    await Write(res, 200, _events.Recent(n));
                    break;

                case ("GET", "/api/clients"):
                    await Write(res, 200, ClientsWithTraffic());
                    break;

                case ("GET", "/api/traffic"):
                    await Write(res, 200, TrafficRows());
                    break;

                case ("POST", "/api/clients"):
                {
                    var body = await ReadJson<ClientCreate>(req);
                    if (body is null || string.IsNullOrWhiteSpace(body.PublicKey))
                    {
                        await Write(res, 400, new { error = "publicKey required" });
                        break;
                    }

                    _clients.Add(new ClientAccount { PublicKeyHex = body.PublicKey.Trim(), Name = body.Name ?? "" });
                    await Write(res, 200, new { ok = true });
                    break;
                }

                case ("PATCH", var p) when p.StartsWith("/api/clients/"):
                {
                    string key = p["/api/clients/".Length..];
                    var body = await ReadJson<ClientPatch>(req);
                    if (body is null)
                    {
                        await Write(res, 400, new { error = "bad body" });
                        break;
                    }

                    bool ok = _clients.SetEnabled(key, body.Enabled);
                    await Write(res, ok ? 200 : 404, new { ok });
                    break;
                }

                case ("DELETE", var p) when p.StartsWith("/api/clients/"):
                {
                    string key = p["/api/clients/".Length..];
                    bool ok = _clients.Remove(key);
                    await Write(res, ok ? 200 : 404, new { ok });
                    break;
                }

                default:
                    await Write(res, 404, new { error = "not found" });
                    break;
            }
        }
        catch (Exception ex)
        {
            try
            {
                await Write(res, 500, new { error = ex.Message });
            }
            catch
            {
                // ignored
            }
        }
    }

    // Живой трафик активных сессий, сгруппированный по ключу клиента.
    private (Dictionary<string, long> down, Dictionary<string, long> up) LiveByClient()
    {
        var down = new Dictionary<string, long>();
        var up = new Dictionary<string, long>();
        foreach (var s in _server.ActiveSessions())
        {
            string k = s.ClientKey.ToLowerInvariant();
            down[k] = (down.TryGetValue(k, out var d) ? d : 0) + s.BytesToClient;
            up[k] = (up.TryGetValue(k, out var u) ? u : 0) + s.BytesFromClient;
        }

        return (down, up);
    }

    // Список клиентов с итоговым трафиком (сохранённый за завершённые сессии + живой активных).
    private object ClientsWithTraffic()
    {
        var (liveDown, liveUp) = LiveByClient();
        return _clients.List().Select(c =>
        {
            string k = c.PublicKeyHex.ToLowerInvariant();
            var t = _traffic.Get(k);
            long down = t.Down + (liveDown.TryGetValue(k, out var ld) ? ld : 0);
            long up = t.Up + (liveUp.TryGetValue(k, out var lu) ? lu : 0);
            bool online = liveDown.ContainsKey(k) || liveUp.ContainsKey(k);
            return new
            {
                publicKeyHex = c.PublicKeyHex, name = c.Name, enabled = c.Enabled, addedUtc = c.AddedUtc,
                totalDown = down, totalUp = up, online,
                lastSeenUtc = online ? DateTime.UtcNow : (t.LastSeenUtc == default ? (DateTime?)null : t.LastSeenUtc),
            };
        }).ToList();
    }

    // Трафик по всем ключам (в т.ч. кого уже нет в allowlist), для отдельного экрана/аналитики.
    private object TrafficRows()
    {
        var (liveDown, liveUp) = LiveByClient();
        var keys = new HashSet<string>(_traffic.Snapshot().Keys);
        foreach (var k in liveDown.Keys) keys.Add(k);
        return keys.Select(k =>
        {
            var t = _traffic.Get(k);
            long down = t.Down + (liveDown.TryGetValue(k, out var ld) ? ld : 0);
            long up = t.Up + (liveUp.TryGetValue(k, out var lu) ? lu : 0);
            return new
            {
                clientKey = k, totalDown = down, totalUp = up,
                online = liveDown.ContainsKey(k) || liveUp.ContainsKey(k), lastSeenUtc = t.LastSeenUtc
            };
        }).OrderByDescending(x => x.totalDown + x.totalUp).ToList();
    }

    private static async Task<T?> ReadJson<T>(HttpListenerRequest req)
    {
        using var reader = new StreamReader(req.InputStream, req.ContentEncoding);
        string body = await reader.ReadToEndAsync().ConfigureAwait(false);
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
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload, Json);
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        res.Close();
    }

    private sealed record ClientCreate(string PublicKey, string? Name);

    private sealed record ClientPatch(bool Enabled);

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