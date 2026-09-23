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

string listen = Env("CHAMELEON_LISTEN", "0.0.0.0:8443");
string keyFile = Env("CHAMELEON_KEY_FILE", "/data/server.key");
string sni = Env("CHAMELEON_SNI", "www.example-cdn.com");
string publicAddr = Env("CHAMELEON_PUBLIC", $"{sni}:443");
string profileName = Env("CHAMELEON_NAME", "Chameleon");
string? certPfx = Environment.GetEnvironmentVariable("CHAMELEON_CERT_PFX");
string? certPass = Environment.GetEnvironmentVariable("CHAMELEON_CERT_PASS");
string? certPem = Environment.GetEnvironmentVariable("CHAMELEON_CERT_PEM");
string? keyPem = Environment.GetEnvironmentVariable("CHAMELEON_KEY_PEM");
string? decoy = Environment.GetEnvironmentVariable("CHAMELEON_DECOY");

KeyPair serverStatic = LoadOrCreateKey(keyFile);
Console.WriteLine("=== Chameleon server ===");
Console.WriteLine($"listen : {listen}");
Console.WriteLine($"sni    : {sni}");
Console.WriteLine($"decoy  : {decoy ?? "(статическая страница)"}");
Console.WriteLine();
string pubKeyHex = Convert.ToHexString(serverStatic.Public).ToLowerInvariant();
Console.WriteLine("СТАТИЧЕСКИЙ ПУБЛИЧНЫЙ КЛЮЧ СЕРВЕРА:");
Console.WriteLine($"  {pubKeyHex}");
Console.WriteLine();
Console.WriteLine("КОНФИГ-ССЫЛКА (скопируйте целиком в клиент -> «Вставить из буфера»):");
Console.WriteLine($"  {BuildLink(publicAddr, pubKeyHex, sni, profileName)}");
Console.WriteLine();

using X509Certificate2 cert = LoadCert(certPem, keyPem, certPfx, certPass, sni);
IPEndPoint endpoint = ParseListen(listen);
DnsEndPoint? decoyEndpoint = decoy is null ? null : ParseDecoy(decoy);

var events = new Chameleon.Core.Proxy.ServerEventLog();
events.Logged += e =>
    Console.WriteLine($"{e.TimeUtc:yyyy-MM-dd HH:mm:ss}Z  [{e.Event,-10}] {e.RemoteIp,-15}  {e.Detail}");

await using var server =
    ChameleonServer.Start(endpoint, serverStatic, TlsCarrier.Server(cert), decoyEndpoint, events: events);
Console.WriteLine($"сервер запущен на {server.EndPoint}. Ctrl+C для остановки.");

var stop = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.TrySetResult();
};
await stop.Task;

static string Env(string name, string fallback) =>
    Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : fallback;

static KeyPair LoadOrCreateKey(string path)
{
    if (File.Exists(path))
    {
        byte[] priv = Convert.FromHexString(File.ReadAllText(path).Trim());
        return new KeyPair(priv, X25519.ScalarMultBase(priv));
    }

    byte[] fresh = RandomNumberGenerator.GetBytes(X25519.KeySize);
    string? dir = Path.GetDirectoryName(path);
    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    File.WriteAllText(path, Convert.ToHexString(fresh).ToLowerInvariant());
    Console.WriteLine($"(сгенерирован новый ключ сервера -> {path})");
    return new KeyPair(fresh, X25519.ScalarMultBase(fresh));
}

static X509Certificate2 LoadCert(string? certPem, string? keyPem, string? pfx, string? pass, string sni)
{
    if (certPem is { Length: > 0 } && keyPem is { Length: > 0 } && File.Exists(certPem) && File.Exists(keyPem))
    {
        using X509Certificate2 fromPem = X509Certificate2.CreateFromPemFile(certPem, keyPem);
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
    int i = s.LastIndexOf(':');
    string host = s[..i];
    int port = int.Parse(s[(i + 1)..]);
    IPAddress ip = host is "0.0.0.0" or "*" ? IPAddress.Any : IPAddress.Parse(host);
    return new IPEndPoint(ip, port);
}

static DnsEndPoint ParseDecoy(string s)
{
    int i = s.LastIndexOf(':');
    return new DnsEndPoint(s[..i], int.Parse(s[(i + 1)..]));
}

static string BuildLink(string publicAddr, string keyHex, string sni, string name)
{
    int i = publicAddr.LastIndexOf(':');
    string host = i > 0 ? publicAddr[..i] : publicAddr;
    int port = i > 0 && int.TryParse(publicAddr[(i + 1)..], out int p) ? p : 443;
    return new ChameleonLink(host, port, keyHex, sni,
        [], name).Build();
}