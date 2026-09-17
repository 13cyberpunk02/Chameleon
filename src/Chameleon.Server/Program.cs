//   Сервер Chameleon. Конфигурация через переменные окружения (удобно для Docker):
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

var listen = Env("CHAMELEON_LISTEN", "0.0.0.0:8443");
var keyFile = Env("CHAMELEON_KEY_FILE", "/data/server.key");
var sni = Env("CHAMELEON_SNI", "www.example-cdn.com");
var certPfx = Environment.GetEnvironmentVariable("CHAMELEON_CERT_PFX");
var certPass = Environment.GetEnvironmentVariable("CHAMELEON_CERT_PASS");
var decoy = Environment.GetEnvironmentVariable("CHAMELEON_DECOY");

var serverStatic = LoadOrCreateKey(keyFile);
Console.WriteLine("=== Chameleon server ===");
Console.WriteLine($"listen : {listen}");
Console.WriteLine($"sni    : {sni}");
Console.WriteLine($"decoy  : {decoy ?? "(статическая страница)"}");
Console.WriteLine();
Console.WriteLine("СТАТИЧЕСКИЙ ПУБЛИЧНЫЙ КЛЮЧ СЕРВЕРА (передайте клиенту):");
Console.WriteLine($"  {Convert.ToHexString(serverStatic.Public).ToLowerInvariant()}");
Console.WriteLine();

using X509Certificate2 cert = LoadCert(certPfx, certPass, sni);
IPEndPoint endpoint = ParseListen(listen);
DnsEndPoint? decoyEndpoint = decoy is null ? null : ParseDecoy(decoy);

await using var server = ChameleonServer.Start(endpoint, serverStatic, TlsCarrier.Server(cert), decoyEndpoint);
Console.WriteLine($"сервер запущен на {server.EndPoint}. Ctrl+C для остановки.");

var stop = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.TrySetResult(); };
await stop.Task;

static string Env(string name, string fallback) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : fallback;

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

static X509Certificate2 LoadCert(string? pfx, string? pass, string sni)
{
    if (pfx is { Length: > 0 } && File.Exists(pfx))
        return X509CertificateLoader.LoadPkcs12FromFile(pfx, pass);
    return TlsCarrier.CreateSelfSignedCertificate(sni);
}

static IPEndPoint ParseListen(string s)
{
    int i = s.LastIndexOf(':');
    string host = s[..i]; int port = int.Parse(s[(i + 1)..]);
    IPAddress ip = host is "0.0.0.0" or "*" ? IPAddress.Any : IPAddress.Parse(host);
    return new IPEndPoint(ip, port);
}

static DnsEndPoint ParseDecoy(string s)
{
    int i = s.LastIndexOf(':');
    return new DnsEndPoint(s[..i], int.Parse(s[(i + 1)..]));
}
