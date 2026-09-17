// CLI VPN-режима (весь трафик через TUN). Требует прав администратора и наличия
// tun2socks (+ wintun.dll рядом с ним на Windows). Это же ядро дёргает GUI.
//
//   Chameleon.Tun <server_host:port> <server_pubkey_hex> [sni] [socks]
//                 [--tun2socks <путь к tun2socks.exe>]
//
// Путь к tun2socks можно задать также переменной CHAMELEON_TUN2SOCKS.

using Chameleon.Tunnel;

var positional = new List<string>();
string? tun2socks = Environment.GetEnvironmentVariable("CHAMELEON_TUN2SOCKS");

for (int i = 0; i < args.Length; i++)
{
    if (args[i] is "--tun2socks" or "-t")
    {
        if (i + 1 >= args.Length)
        {
            Console.Error.WriteLine("--tun2socks требует путь");
            return 2;
        }

        tun2socks = args[++i];
    }
    else positional.Add(args[i]);
}

if (positional.Count < 2)
{
    Console.WriteLine("Использование:");
    Console.WriteLine("  Chameleon.Tun <server_host:port> <server_pubkey_hex> [sni] [socks] [--tun2socks <путь>]");
    Console.WriteLine();
    Console.WriteLine(
        "Требуются: права администратора; tun2socks (xjasonlyu); рядом с tun2socks.exe - wintun.dll (Windows).");
    Console.WriteLine(
        "Путь к tun2socks: параметр --tun2socks или переменная CHAMELEON_TUN2SOCKS (иначе ищется в PATH).");
    return 1;
}

var options = new TunnelOptions
{
    Server = positional[0],
    ServerPublicKeyHex = positional[1],
    Sni = positional.Count > 2 ? positional[2] : "www.example-cdn.com",
    SocksListen = positional.Count > 3 ? positional[3] : "127.0.0.1:1080",
    Tun2SocksPath = tun2socks is { Length: > 0 }
        ? tun2socks
        : (OperatingSystem.IsWindows() ? "tun2socks.exe" : "tun2socks"),
};

var service = new TunnelService();
service.Log += (_, m) => Console.WriteLine(m);
service.StatusChanged += (_, s) => Console.WriteLine($"[статус] {s}");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

try
{
    await service.ConnectAsync(options, cts.Token);
    Console.WriteLine("Подключено. Ctrl+C - отключиться.");
    try
    {
        await Task.Delay(Timeout.Infinite, cts.Token);
    }
    catch (OperationCanceledException)
    {
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Не удалось подключиться: {ex.Message}");
    return 3;
}
finally
{
    Console.WriteLine("Отключение…");
    await service.DisconnectAsync();
}

return 0;