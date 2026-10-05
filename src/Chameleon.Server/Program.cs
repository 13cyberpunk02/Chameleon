// Сервер Chameleon. Конфигурация через переменные окружения (удобно для Docker):
//   CHAMELEON_LISTEN   адрес прослушивания        (по умолчанию 0.0.0.0:8443)
//   CHAMELEON_KEY_FILE файл со статическим ключом  (по умолчанию /data/server.key)
//   CHAMELEON_SNI      домен прикрытия / CN серта  (по умолчанию www.example-cdn.com)
//   CHAMELEON_CERT_PFX путь к .pfx (боевой серт)   (иначе - самоподписанный по SNI)
//   CHAMELEON_CERT_PASS пароль к .pfx
//   CHAMELEON_DECOY    сайт-декой host:port        (иначе - статическая заглушка)

using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Chameleon.Core.Crypto;
using Chameleon.Core.Proxy;
using Chameleon.Core.Transport;
using Chameleon.Server;

var listen = Env("CHAMELEON_LISTEN", "0.0.0.0:8443");
var keyFile = Env("CHAMELEON_KEY_FILE", "/data/server.key");
var sni = Env("CHAMELEON_SNI", "www.example-cdn.com");
var publicAddr = Env("CHAMELEON_PUBLIC", $"{sni}:443"); // адрес, на который подключается клиент
var profileName = Env("CHAMELEON_NAME", "Chameleon");
var certPfx = Environment.GetEnvironmentVariable("CHAMELEON_CERT_PFX");
var certPass = Environment.GetEnvironmentVariable("CHAMELEON_CERT_PASS");
var certPem = Environment.GetEnvironmentVariable("CHAMELEON_CERT_PEM");
var keyPem = Environment.GetEnvironmentVariable("CHAMELEON_KEY_PEM");
var decoy = Environment.GetEnvironmentVariable("CHAMELEON_DECOY");
var proxyProtocol = Env("CHAMELEON_PROXY_PROTOCOL", "0") is "1" or "true";
var allowlist = Env("CHAMELEON_ALLOWLIST", "0") is "1" or "true";
var clientsFile = Env("CHAMELEON_CLIENTS_FILE", "/data/clients.json");
var trafficFile = Env("CHAMELEON_TRAFFIC_FILE", "/data/traffic.json");
var dbFile = Env("CHAMELEON_DB", "/data/chameleon.db");

var serverStatic = LoadOrCreateKey(keyFile);
Console.WriteLine("=== Chameleon server ===");
Console.WriteLine($"listen : {listen}");
Console.WriteLine($"sni    : {sni}");
Console.WriteLine($"decoy  : {decoy ?? "(статическая страница)"}");
Console.WriteLine($"proxy-protocol: {(proxyProtocol ? "включён (ждём PROXY-заголовок от nginx)" : "выключен")}");
Console.WriteLine();
var pubKeyHex = Convert.ToHexString(serverStatic.Public).ToLowerInvariant();
Console.WriteLine("СТАТИЧЕСКИЙ ПУБЛИЧНЫЙ КЛЮЧ СЕРВЕРА:");
Console.WriteLine($"  {pubKeyHex}");
Console.WriteLine();
Console.WriteLine("КОНФИГ-ССЫЛКА (скопируйте целиком в клиент → «Вставить из буфера»):");
Console.WriteLine($"  {BuildLink(publicAddr, pubKeyHex, sni, profileName)}");
Console.WriteLine();

using var cert = LoadCert(certPem, keyPem, certPfx, certPass, sni);
var endpoint = ParseListen(listen);
var decoyEndpoint = decoy is null ? null : ParseDecoy(decoy);

var store = new SqliteStore(dbFile, enforced: allowlist);
store.MigrateFromJson(clientsFile, trafficFile);
foreach (var k in (Environment.GetEnvironmentVariable("CHAMELEON_CLIENTS") ?? "").Split(',',
             StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    store.Add(new Chameleon.Core.Proxy.ClientAccount { PublicKeyHex = k, Name = "env" });
Console.WriteLine(
    $"allowlist: {(store.Enforced ? $"включён, клиентов: {store.Count}" : "выключен (принимаем любого)")}");
Console.WriteLine($"БД: {dbFile}");

var events = new Chameleon.Core.Proxy.ServerEventLog();
events.Logged += e =>
    Console.WriteLine($"{e.TimeUtc:yyyy-MM-dd HH:mm:ss}Z  [{e.Event,-10}] {e.RemoteIp,-15}  {e.Detail}");

await using var server = ChameleonServer.Start(endpoint, serverStatic, TlsCarrier.Server(cert), decoyEndpoint,
    events: events, proxyProtocol: proxyProtocol, clients: store);

server.SessionClosed += (sid, key, down, up) => store.AddClosedSession(key, down, up);

var histSampleSec = int.TryParse(Env("CHAMELEON_HISTORY_SAMPLE", "15"), out var hs) ? Math.Max(5, hs) : 15;
var historySampler = new HistorySampler(server, store, TimeSpan.FromSeconds(histSampleSec));
historySampler.Start();

var enforceSec = int.TryParse(Env("CHAMELEON_ENFORCE_INTERVAL", "10"), out var es) ? Math.Max(3, es) : 10;
var enforcement = new EnforcementRunner(server, store,
    [new LimitPolicy()], TimeSpan.FromSeconds(enforceSec));
enforcement.Start();
Console.WriteLine($"enforcement: политик={1}, интервал={enforceSec}с");

var historyDays = int.TryParse(Env("CHAMELEON_HISTORY_DAYS", "90"), out var hd) ? Math.Max(7, hd) : 90;
store.PurgeHistory(historyDays);
using var purgeTimer = new Timer(_ =>
    {
        try
        {
            store.PurgeHistory(historyDays);
        }
        catch
        {
            // ignored
        }
    },
    null, TimeSpan.FromHours(24), TimeSpan.FromHours(24));
Console.WriteLine($"история трафика: хранение {historyDays} дней");
Console.WriteLine($"сервер запущен на {server.EndPoint}. Ctrl+C для остановки.");

var apiToken = Environment.GetEnvironmentVariable("CHAMELEON_API_TOKEN");
var apiListen = Env("CHAMELEON_API_LISTEN", "http://+:9090/");
ManagementApi? api = null;
if (!string.IsNullOrWhiteSpace(apiToken))
{
    api = new ManagementApi(apiListen, apiToken, server, events, store, pubKeyHex, sni, publicAddr);
    api.Start();
    Console.WriteLine($"management API: {apiListen} (Bearer-токен задан)");
}
else Console.WriteLine("management API: выключен (задай CHAMELEON_API_TOKEN, чтобы включить)");

using var statsTimer = new Timer(_ =>
{
    var st = server.Stats();
    if (st.ActiveSessions == 0) return;
    Console.WriteLine($"{DateTime.UtcNow:HH:mm:ss}Z  [stats] сессий={st.ActiveSessions}, потоков={st.TotalStreams}, " +
                      $"клиентам↓={Bytes(st.TotalBytesToClient)}, от клиентов↑={Bytes(st.TotalBytesFromClient)}");
}, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

var stop = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.TrySetResult();
};
await stop.Task;
api?.Dispose();
historySampler.Dispose();
enforcement.Dispose();
store.Dispose();

static string Env(string name, string fallback) =>
    Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : fallback;

static KeyPair LoadOrCreateKey(string path)
{
    if (File.Exists(path))
    {
        var priv = Convert.FromHexString(File.ReadAllText(path).Trim());
        return new KeyPair(priv, X25519.ScalarMultBase(priv));
    }

    var fresh = RandomNumberGenerator.GetBytes(X25519.KeySize);
    var dir = Path.GetDirectoryName(path);
    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    File.WriteAllText(path, Convert.ToHexString(fresh).ToLowerInvariant());
    Console.WriteLine($"(сгенерирован новый ключ сервера → {path})");
    return new KeyPair(fresh, X25519.ScalarMultBase(fresh));
}

static X509Certificate2 LoadCert(string? certPem, string? keyPem, string? pfx, string? pass, string sni)
{
    if (certPem is { Length: > 0 } && keyPem is { Length: > 0 } && File.Exists(certPem) && File.Exists(keyPem))
    {
        using var fromPem = X509Certificate2.CreateFromPemFile(certPem, keyPem);
        return LoadPfxBytes(fromPem.Export(X509ContentType.Pfx), null);
    }

    if (pfx is { Length: > 0 } && File.Exists(pfx))
        return LoadPfxBytes(File.ReadAllBytes(pfx), pass);
    return TlsCarrier.CreateSelfSignedCertificate(sni);
}

static X509Certificate2 LoadPfxBytes(byte[] pfx, string? pass) =>
    X509CertificateLoader.LoadPkcs12(pfx, pass, X509KeyStorageFlags.Exportable);

static IPEndPoint ParseListen(string s)
{
    var i = s.LastIndexOf(':');
    var host = s[..i];
    var port = int.Parse(s[(i + 1)..]);
    var ip = host is "0.0.0.0" or "*" ? IPAddress.Any : IPAddress.Parse(host);
    return new IPEndPoint(ip, port);
}

static DnsEndPoint ParseDecoy(string s)
{
    var i = s.LastIndexOf(':');
    return new DnsEndPoint(s[..i], int.Parse(s[(i + 1)..]));
}

static string BuildLink(string publicAddr, string keyHex, string sni, string name)
{
    var i = publicAddr.LastIndexOf(':');
    var host = i > 0 ? publicAddr[..i] : publicAddr;
    var port = i > 0 && int.TryParse(publicAddr[(i + 1)..], out var p) ? p : 443;
    return new Chameleon.Core.Proxy.ChameleonLink(host, port, keyHex, sni,
        [], name).Build();
}

static string Bytes(long b)
{
    string[] u = ["B", "KB", "MB", "GB", "TB"];
    var v = b;
    var k = 0;
    while (v >= 1024 && k < u.Length - 1)
    {
        v /= 1024;
        k++;
    }

    return $"{v:0.#}{u[k]}";
}