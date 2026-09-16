using Chameleon.Playground.Examples;

var examples = new (string Name, string Description, Func<Task> Run)[]
{
    ("handshake", "Рукопожатие Noise IK + record-слой + проверка на зонд", HandshakeExample.RunAsync),
    ("socks5", "Сквозной SOCKS5-прокси поверх голого TCP", Socks5ProxyExample.RunAsync),
    ("tls", "Прокси поверх TLS + декой-прокси (зонд видит настоящий сайт)", TlsProxyExample.RunAsync),
    ("ja4", "Отпечаток ClientHello: JA3/JA4 против эталона Chromium", Ja4Example.RunAsync),
    ("shaper", "Шейпер: стохастический автомат поведения + смена модели", ShaperExample.RunAsync),
    ("models", "Модели поведения: обучение из сэмплов, JSON, загрузка", TrafficModelExample.RunAsync),
    ("policing", "Детектор полисинга vs перегрузки", PolicingExample.RunAsync),
    ("multipath", "Мультипуть: две несущие, убийство одной посреди передачи", MultipathExample.RunAsync),
    ("join", "Присоединение несущих по сети: три несущие в одной сессии", CarrierJoinExample.RunAsync),
    ("fec", "FEC: Reed-Solomon, восстановление потерь без ретрансмита", FecExample.RunAsync),
};

string? pick = args.Length > 0 ? args[0].ToLowerInvariant() : null;
var chosen = examples.FirstOrDefault(e => e.Name == pick);

if (chosen.Run is null)
{
    Console.WriteLine("Примеры использования Chameleon. Запуск:\n");
    Console.WriteLine("  dotnet run --project src/Chameleon.Playground -- <имя>\n");
    foreach (var e in examples)
        Console.WriteLine($"  {e.Name,-10} - {e.Description}");
    Console.WriteLine("\n  all        - прогнать все по очереди");
    if (pick == "all")
    {
        foreach (var e in examples)
        {
            Console.WriteLine($"\n\n########## {e.Name} ##########");
            await e.Run();
        }
    }

    return;
}

await chosen.Run();