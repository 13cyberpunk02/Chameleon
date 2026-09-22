namespace Chameleon.Tunnel;

/// <summary>Linux - ЗАДЕЛ НА БУДУЩЕЕ. Не реализовано (см. TUN.md).</summary>
public sealed class LinuxPlatformNet : IPlatformNet
{
    private static Task Nope() => throw new PlatformNotSupportedException(
        "TUN для Linux пока не реализован - это задел на будущее (ip route + /dev/net/tun).");

    public Task<(string, int)> GetDefaultRouteAsync(CancellationToken ct) => throw new PlatformNotSupportedException(
        "TUN для Linux пока не реализован - задел на будущее.");
    public Task AddHostRouteAsync(string d, string g, int i, CancellationToken ct) => Nope();
    public Task RemoveHostRouteAsync(string d, CancellationToken ct) => Nope();
    public Task AddBypassRouteAsync(string n, int p, string g, int i, CancellationToken ct) => Nope();
    public Task RemoveBypassRouteAsync(string n, int p, CancellationToken ct) => Nope();
    public Task ConfigureTunAsync(TunnelOptions o, int i, CancellationToken ct) => Nope();
    public Task AddDefaultViaTunAsync(TunnelOptions o, CancellationToken ct) => Nope();
    public Task RemoveDefaultViaTunAsync(TunnelOptions o, CancellationToken ct) => Nope();
}