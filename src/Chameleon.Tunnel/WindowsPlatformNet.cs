using System.Net.NetworkInformation;
using System.Runtime.Versioning;

namespace Chameleon.Tunnel;

/// <summary>
/// Реализация под Windows: маршруты через `route`, настройка адаптера через `netsh`.
/// Требует прав администратора. НЕ ПРОВЕРЕНО в песочнице - тестировать на реальной
/// Windows и при необходимости подправить имена/индексы адаптера.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsPlatformNet : IPlatformNet
{
    public Action<string>? Log { get; set; }

    public async Task<(string GatewayIp, int InterfaceIndex)> GetDefaultRouteAsync(CancellationToken ct)
    {
        try
        {
            string output = await ProcessRunner.RunCaptureAsync("route", "print -4", ct).ConfigureAwait(false);
            var rx = new System.Text.RegularExpressions.Regex(
                @"^\s*0\.0\.0\.0\s+0\.0\.0\.0\s+(\d+\.\d+\.\d+\.\d+)\s+(\d+\.\d+\.\d+\.\d+)\s+(\d+)",
                System.Text.RegularExpressions.RegexOptions.Multiline);
            (string gw, string ifaceIp, int metric)? best = null;
            foreach (System.Text.RegularExpressions.Match m in rx.Matches(output))
            {
                int metric = int.Parse(m.Groups[3].Value);
                if (best is null || metric < best.Value.metric)
                    best = (m.Groups[1].Value, m.Groups[2].Value, metric);
            }

            if (best is { } b)
            {
                int idx = IndexByLocalIp(b.ifaceIp);
                if (idx > 0)
                {
                    Log?.Invoke($"default-маршрут: шлюз {b.gw}, интерфейс {b.ifaceIp} (if {idx}, metric {b.metric})");
                    return (b.gw, idx);
                }
            }
        }
        catch (Exception e)
        {
            Log?.Invoke($"route print не разобран ({e.Message}), fallback на NetworkInterface");
        }
        
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            var props = ni.GetIPProperties();
            var gw = props.GatewayAddresses.FirstOrDefault(g =>
                g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                !g.Address.Equals(System.Net.IPAddress.Any));
            if (gw is null) continue;
            return (gw.Address.ToString(), props.GetIPv4Properties().Index);
        }

        throw new InvalidOperationException("Не найден шлюз по умолчанию");
    }

    private static int IndexByLocalIp(string ip)
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            IPInterfaceProperties props;
            try
            {
                props = ni.GetIPProperties();
            }
            catch
            {
                continue;
            }

            if (props.UnicastAddresses.Any(u => u.Address.ToString() == ip))
            {
                try
                {
                    return props.GetIPv4Properties().Index;
                }
                catch
                {
                    return 0;
                }
            }
        }

        return 0;
    }

    public Task AddHostRouteAsync(string destinationIp, string gatewayIp, int interfaceIndex, CancellationToken ct)
        => ProcessRunner.RunAsync("route",
            $"add {destinationIp} mask 255.255.255.255 {gatewayIp} metric 1 if {interfaceIndex}", Log, ct);

    public Task RemoveHostRouteAsync(string destinationIp, CancellationToken ct)
        => ProcessRunner.RunAsync("route", $"delete {destinationIp}", Log, ct);

    public Task AddBypassRouteAsync(string network, int prefix, string gatewayIp, int interfaceIndex,
        CancellationToken ct)
        => ProcessRunner.RunAsync("route",
            $"add {network} mask {PrefixToMask(prefix)} {gatewayIp} metric 1 if {interfaceIndex}", Log, ct);

    public Task RemoveBypassRouteAsync(string network, int prefix, CancellationToken ct)
        => ProcessRunner.RunAsync("route", $"delete {network} mask {PrefixToMask(prefix)}", Log, ct);

    public async Task ConfigureTunAsync(TunnelOptions o, int interfaceIndex, CancellationToken ct)
    {
        _tunIndex = interfaceIndex;
        string mask = PrefixToMask(o.TunPrefix);

        for (int attempt = 1; attempt <= 10; attempt++)
        {
            await ProcessRunner.RunAsync("netsh",
                $"interface ipv4 set address {interfaceIndex} static {o.TunAddress} {mask}", Log, ct);

            if (HasAddress(interfaceIndex, o.TunAddress))
            {
                Log?.Invoke($"адрес {o.TunAddress} назначен адаптеру (if {interfaceIndex}), попытка {attempt}");
                break;
            }

            if (attempt == 10)
                throw new InvalidOperationException(
                    $"не удалось назначить адрес {o.TunAddress} на if {interfaceIndex}");
            await Task.Delay(300, ct).ConfigureAwait(false);
        }

        await ProcessRunner.RunAsync("netsh",
            $"interface ipv4 set dnsservers {interfaceIndex} static {o.TunDns} primary", Log, ct);
    }

    private static bool HasAddress(int interfaceIndex, string address)
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            IPInterfaceProperties props;
            try
            {
                props = ni.GetIPProperties();
            }
            catch
            {
                continue;
            }

            int idx;
            try
            {
                idx = props.GetIPv4Properties().Index;
            }
            catch
            {
                continue;
            }

            if (idx != interfaceIndex) continue;
            return props.UnicastAddresses.Any(u => u.Address.ToString() == address);
        }

        return false;
    }

    // ВАЖНО: маршруты привязываем к ИНДЕКСУ TUN-адаптера, иначе Windows по next-hop
    // может выбрать не тот интерфейс - и трафик уйдёт мимо TUN (реальный баг был именно тут).
    private int _tunIndex;

    public async Task AddDefaultViaTunAsync(TunnelOptions o, CancellationToken ct)
    {
        await ProcessRunner.RunAsync("netsh",
            $"interface ipv4 add route 0.0.0.0/1 {_tunIndex} {o.TunAddress} metric=1 store=active", Log, ct);
        await ProcessRunner.RunAsync("netsh",
            $"interface ipv4 add route 128.0.0.0/1 {_tunIndex} {o.TunAddress} metric=1 store=active", Log, ct);

        bool ok = await HasRouteAsync("0.0.0.0/1", _tunIndex, ct).ConfigureAwait(false)
                  && await HasRouteAsync("128.0.0.0/1", _tunIndex, ct).ConfigureAwait(false);
        Log?.Invoke(ok
            ? $"маршрут по умолчанию заведён через TUN (if {_tunIndex})"
            : $"ВНИМАНИЕ: split-default маршруты не подтвердились на if {_tunIndex} - трафик может идти мимо TUN");
    }

    public async Task RemoveDefaultViaTunAsync(TunnelOptions o, CancellationToken ct)
    {
        await ProcessRunner.RunAsync("netsh", $"interface ipv4 delete route 0.0.0.0/1 {_tunIndex}", Log, ct);
        await ProcessRunner.RunAsync("netsh", $"interface ipv4 delete route 128.0.0.0/1 {_tunIndex}", Log, ct);
    }

    /// <summary>Проверяет, что маршрут prefix реально стоит на интерфейсе ifIndex (netsh show route).</summary>
    private static async Task<bool> HasRouteAsync(string prefix, int ifIndex, CancellationToken ct)
    {
        try
        {
            string output = await ProcessRunner.RunCaptureAsync("netsh", "interface ipv4 show route", ct)
                .ConfigureAwait(false);
            foreach (string line in output.Split('\n'))
                if (line.Contains(prefix) && System.Text.RegularExpressions.Regex.IsMatch(line, $@"{ifIndex}"))
                    return true;
            return false;
        }
        catch
        {
            return true;
        }
    }

    private static string PrefixToMask(int prefix)
    {
        uint m = prefix == 0 ? 0 : 0xffffffff << (32 - prefix);
        return $"{(m >> 24) & 0xff}.{(m >> 16) & 0xff}.{(m >> 8) & 0xff}.{m & 0xff}";
    }
}