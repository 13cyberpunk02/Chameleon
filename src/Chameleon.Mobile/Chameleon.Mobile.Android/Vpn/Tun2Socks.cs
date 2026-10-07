using System.Runtime.InteropServices;

namespace Chameleon.Mobile.Android.Vpn;

/// <summary>
/// P/Invoke к нативной hev-socks5-tunnel (heiher/hev-socks5-tunnel).
/// Берёт fd TUN-интерфейса (от VpnService) + адрес нашего SOCKS и гоняет трафик.
///
/// Нативный файл: lib/arm64-v8a/libhev-socks5-tunnel.so
/// (имя для DllImport — без 'lib' и '.so': "hev-socks5-tunnel").
///
/// Экспортируемые функции библиотеки:
///   int  hev_socks5_tunnel_main_from_str(const uint8_t *config, unsigned len);  // блокирующий
///   void hev_socks5_tunnel_quit(void);
/// </summary>
internal static class Tun2Socks
{
    private const string Lib = "hev-socks5-tunnel";

    [DllImport(Lib, EntryPoint = "hev_socks5_tunnel_main_from_str", CallingConvention = CallingConvention.Cdecl)]
    private static extern int MainFromStr(byte[] config, uint len, int tunFd);

    [DllImport(Lib, EntryPoint = "hev_socks5_tunnel_quit", CallingConvention = CallingConvention.Cdecl)]
    private static extern void Quit();

    /// <summary>
    /// Запуск (БЛОКИРУЮЩИЙ — вызывать на фоновом потоке). Возвращает, когда туннель
    /// остановлен (Stop) или произошла ошибка.
    /// </summary>
    public static int Run(int tunFd, string socksAddr, int socksPort, int mtu = 1500, string? logFile = null)
    {
        // Конфиг hev-socks5-tunnel (YAML). fd передаём напрямую — библиотека не
        // будет сама открывать/создавать устройство.
        // fd передаётся ТРЕТЬИМ АРГУМЕНТОМ функции, НЕ в конфиге (hev-socks5-tunnel API).
        string yaml =
            "tunnel:\n" +
            $"  mtu: {mtu}\n" +
            "socks5:\n" +
            $"  address: {socksAddr}\n" +
            $"  port: {socksPort}\n" +
            "  udp: 'udp'\n" +
            "misc:\n" +
            "  task-stack-size: 20480\n" +
            "  log-level: debug\n" +
            (string.IsNullOrEmpty(logFile) ? "" : $"  log-file: {logFile}\n");

        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(yaml);
        return MainFromStr(bytes, (uint)bytes.Length, tunFd);
    }

    public static void Stop()
    {
        try { Quit(); } catch { }
    }
}
