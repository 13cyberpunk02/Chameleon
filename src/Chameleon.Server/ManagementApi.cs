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
    private readonly SqliteStore _store;
    private readonly string _token;
    private readonly string _serverPublicKey;
    private readonly string _sni;
    private readonly DateTime _startedUtc = DateTime.UtcNow;
    private readonly CancellationTokenSource _cts = new();
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public ManagementApi(string prefix, string token, ChameleonServer server, ServerEventLog events,
        SqliteStore store, string serverPublicKey, string sni)
    {
        _server = server;
        _events = events;
        _store = store;
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
                        allowlist = _store.Enforced,
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

                    _store.Add(new ClientAccount { PublicKeyHex = body.PublicKey.Trim(), Name = body.Name ?? "" });
                    await Write(res, 200, new { ok = true });
                    break;
                }

                case ("PATCH", var p) when p.EndsWith("/limit") && p.StartsWith("/api/clients/"):
                {
                    string key = p["/api/clients/".Length..^"/limit".Length];
                    var body = await ReadJson<LimitPatch>(req);
                    if (body is null)
                    {
                        await Write(res, 400, new { error = "bad body" });
                        break;
                    }

                    bool ok = _store.SetLimit(key, body.LimitBytes);
                    await Write(res, ok ? 200 : 404, new { ok });
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

                    bool ok = _store.SetEnabled(key, body.Enabled);
                    await Write(res, ok ? 200 : 404, new { ok });
                    break;
                }

                case ("DELETE", var p) when p.StartsWith("/api/clients/"):
                {
                    string key = p["/api/clients/".Length..];
                    bool ok = _store.Remove(key);
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

    private object ClientsWithTraffic()
    {
        var (liveDown, liveUp) = LiveByClient();
        return _store.Rows().Select(r =>
        {
            string k = r.Key;
            long ld = liveDown.TryGetValue(k, out var d) ? d : 0;
            long lu = liveUp.TryGetValue(k, out var u) ? u : 0;
            bool online = liveDown.ContainsKey(k) || liveUp.ContainsKey(k);
            long periodDown = r.PeriodDown + ld; // download за период + живой
            bool overLimit = r.LimitBytes > 0 && periodDown >= r.LimitBytes;
            long? remaining = r.LimitBytes > 0 ? Math.Max(0, r.LimitBytes - periodDown) : (long?)null;
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