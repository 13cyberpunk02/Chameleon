namespace Chameleon.Tunnel;

/// <summary>Настройки VPN-режима (весь трафик через TUN -> наш SOCKS5 -> сервер).</summary>
public sealed class TunnelOptions
{
    /// <summary>Адрес сервера, host:port (например dl.proxy.site:443).</summary>
    public required string Server { get; init; }

    /// <summary>Публичный статический ключ сервера (hex).</summary>
    public required string ServerPublicKeyHex { get; init; }

    /// <summary>SNI (домен маскировки; должен совпадать с сертификатом сервера).</summary>
    public string Sni { get; init; } = "www.example-cdn.com";

    /// <summary>Локальный SOCKS5, который поднимет клиент.</summary>
    public string SocksListen { get; init; } = "127.0.0.1:1080";

    /// <summary>Доп. несущие (мультипуть), host:port через запятую.</summary>
    public string? ExtraCarriers { get; init; }

    /// <summary>Путь к бинарнику tun2socks (xjasonlyu). По умолчанию ищется в PATH.</summary>
    public string Tun2SocksPath { get; init; } = OperatingSystem.IsWindows() ? "tun2socks.exe" : "tun2socks";

    /// <summary>Имя TUN-устройства.</summary>
    public string TunDeviceName { get; init; } = "chameleon0";

    /// <summary>Адрес, назначаемый TUN-адаптеру (и он же шлюз для маршрута по умолчанию).</summary>
    public string TunAddress { get; init; } = "198.18.0.1";

    public int TunPrefix { get; init; } = 24;

    /// <summary>DNS, прописываемый на TUN-адаптер (идёт в туннель). См. оговорку про UDP.</summary>
    public string TunDns { get; init; } = "1.1.1.1";
}

public enum TunnelStatus
{
    Disconnected,
    Connecting,
    Connected,
    Error
}