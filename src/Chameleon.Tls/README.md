# Chameleon.Tls

TLS-несущая на BouncyCastle с ClientHello под профиль Chrome. Нужна, потому что
System.Net.Security.SslStream не даёт управлять составом ClientHello - и по
отпечатку палится как «не браузер».

## Зависимость
NuGet-пакет `BouncyCastle.Cryptography` (>= 2.7.0). Прописан в csproj.

## Использование (клиент)
```csharp
using Chameleon.Tls;

await using var client = await ChameleonClient.StartAsync(
    serverEndPoint, clientStatic, serverStaticPublic, socksEndPoint,
    carrier: BcTlsCarrier.Client("www.example-cdn.com"),   // ClientHello под Chrome
    shaper: TrafficShaper.WebBrowsing);
```
Сервер остаётся на `TlsCarrier.Server(cert)` (SslStream) - совместимы.

## Отпечаток: результат
Достигнутый JA4 нашего ClientHello:
```
t13d1516h2_8daaf6152771_f66804d42859
```
- `t13d1516h2` - как у Chrome (TCP, TLS 1.3, SNI есть, 15 шифров, 16 расширений, ALPN h2);
- `8daaf6152771` - хеш списка шифров; совпадает с Chrome (список из 15 шифров идентичен);
- третья часть `ja4_c` (хеш расширений+подписей) зависит от точного набора
  конкретной сборки Chrome и подгоняется отдельно (см. ниже).

Измерять: `Ja4.FromClientHelloRecord(bytes)` и `Ja3.FromClientHelloRecord(bytes)`.

## Почему JA4, а не JA3
BouncyCastle не добавляет GREASE и держит СВОЙ порядок расширений (нулевые
вперёд). JA3 чувствителен к порядку → под Chrome его не свести. Но **JA4
сортирует расширения и исключает GREASE** - ровно те две вещи, которыми BC не
управляет. Поэтому по JA4 (современный отпечаток, по нему и фильтруют) мы можем
совпасть с Chrome, и уже совпадаем по ja4_a и ja4_b.

## Как дотянуть ja4_c до конкретного Chrome
ja4_c - хеш от (отсортированные расширения без GREASE/SNI/ALPN) + (алгоритмы
подписи в порядке следования). Чтобы совпасть с целевой сборкой Chrome:
1. снять JA4 настоящего Chrome нужной версии (например, из ja4db или своим сниффером);
2. подогнать набор расширений в `GetClientExtensions` и список
   `signature_algorithms` (переопределить `GetSupportedSignatureAlgorithms`) под него;
3. сверять результат `Ja4.FromClientHelloRecord`, пока ja4_c не совпадёт.
   Порядок и GREASE на JA4 не влияют, поэтому это достижимо на стоковом BouncyCastle.

## Предел: JA3 и «сырые» парсеры
Точный JA3 под Chrome на стоковом BC недостижим (порядок расширений, отсутствие
GREASE в расширениях/группах/key_share). Если цель - обойти фильтр, смотрящий
именно на JA3 или на побайтовую структуру ClientHello с GREASE, нужен либо
патч BouncyCastle, либо внешний uTLS-совместимый слой. По JA4 такого ограничения нет.

## Про поток
BC TLS-поток синхронный; базовый Stream сериализует async read/write общим
семафором. BcTlsCarrier оборачивает поток в BcDuplexStream, иначе одновременные
чтение и запись сессии встают в тупик.
