# Chameleon.Tls

TLS-несущая на BouncyCastle с браузерным ClientHello. Нужна, потому что
System.Net.Security.SslStream не даёт управлять составом и порядком расширений
ClientHello - и по отпечатку JA3/JA4 палится как «не браузер».

Здесь ClientHello формирует BouncyCastle, состав которого мы задаём сами
(cipher suites, группы, ALPN, версии). Клиент BC совместим с обычным
SslStream-сервером, поэтому серверную сторону менять не нужно.

## Зависимость
NuGet-пакет `BouncyCastle.Cryptography` (>= 2.7.0). Уже прописан в csproj.

## Использование (клиент)
```csharp
using Chameleon.Tls;

await using var client = await ChameleonClient.StartAsync(
    serverEndPoint, clientStatic, serverStaticPublic, socksEndPoint,
    carrier: BcTlsCarrier.Client("www.example-cdn.com"),   // <-- браузерный ClientHello
    shaper: TrafficShaper.WebBrowsing);
```
Сервер остаётся на `TlsCarrier.Server(cert)` (SslStream) - они совместимы.

## Проверка отпечатка
`Ja3.FromClientHelloRecord(bytes)` считает JA3 из сырых байт ClientHello.
Текущий JA3 нашего ClientHello:
```
771,4865-4866-4867-49195-49199-255,22-23-16-0-5-13-10-11-43-51,29-23-24,0
```

## ВАЖНО: это ещё не точный отпечаток Chrome
BouncyCastle НЕ добавляет значения GREASE и располагает расширения в своём
порядке, а не в порядке Chrome. Поэтому текущий JA3/JA4 - это «обобщённый
современный TLS 1.3», отличный от SslStream, но НЕ бит-в-бит Chrome.

Чтобы приблизить к конкретному Chrome, надо:
- добавить GREASE в cipher suites, группы и расширения;
- выставить точный список и ПОРЯДОК расширений Chrome;
- подогнать список cipher suites под версию Chrome.
  Это следующий подэтап (2б-3). JA3 из Ja3.cs позволяет сверять результат.

## Замечание про поток
BC TLS-поток синхронный и блокирующий, а базовый Stream сериализует
async read/write общим семафором. Поэтому BcTlsCarrier оборачивает поток в
BcDuplexStream - иначе одновременные чтение и запись сессии встают в тупик.