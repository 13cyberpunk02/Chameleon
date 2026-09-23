using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Chameleon.Core.Crypto;
using Chameleon.Core.Proxy;
using Chameleon.Tls;

namespace Chameleon.Tunnel;

/// <summary>
/// Оркестратор VPN-режима. Одна точка, которую дёргает и CLI, и будущий GUI:
///   ConnectAsync - поднимает клиент (SOCKS5), tun2socks и маршруты;
///   DisconnectAsync - аккуратно всё откатывает в обратном порядке.
///
/// Последовательность важна: сначала клиент (даёт SOCKS5), затем маршрут-исключение
/// до сервера (иначе туннель зациклится), затем tun2socks + маршрут по умолчанию в TUN.
/// </summary>
public sealed class TunnelService : IAsyncDisposable
{
    private readonly IPlatformNet _net;
    private ChameleonClient? _client;

    /// <summary>Активный клиент (для статистики), null пока не подключено.</summary>
    public Chameleon.Core.Proxy.ChameleonClient? Client => _client;

    private Process? _tun2socks;
    private TunnelOptions? _options;
    private string? _serverIp;
    private bool _hostRouteAdded, _defaultRouteAdded;
    private string? _gatewayIp;
    private int _gatewayIfIndex;
    private readonly List<(string Network, int Prefix)> _bypassAdded = new();
    private IPEndPoint? _serverEndpoint, _socksEndpoint;
    private Task? _watchdog;
    private CancellationTokenSource? _watchdogCts;

    public TunnelService(IPlatformNet? net = null)
    {
        _net = net ?? IPlatformNet.Create();
        _net.Log = m => Log?.Invoke(this, m);
    }

    public event EventHandler<string>? Log;
    public event EventHandler<TunnelStatus>? StatusChanged;
    public TunnelStatus Status { get; private set; } = TunnelStatus.Disconnected;

    public async Task ConnectAsync(TunnelOptions options, CancellationToken ct = default)
    {
        if (Status is TunnelStatus.Connected or TunnelStatus.Connecting) return;
        _options = options;
        SetStatus(TunnelStatus.Connecting);
        try
        {
            string exe = ResolveExecutable(options.Tun2SocksPath);
            string workDir = Path.GetDirectoryName(exe) ?? Environment.CurrentDirectory;
            Info($"tun2socks: {exe}");
            Info($"рабочая директория tun2socks: {workDir} (отсюда грузится wintun.dll)");
            if (OperatingSystem.IsWindows())
            {
                string wintun = Path.Combine(workDir, "wintun.dll");
                if (File.Exists(wintun)) Info($"найден wintun.dll: {wintun}");
                else
                    Info(
                        $"ВНИМАНИЕ: рядом с tun2socks нет wintun.dll ({wintun}) - адаптер, скорее всего, не поднимется. Положите wintun.dll той же разрядности в папку с tun2socks.exe.");
            }

            _serverEndpoint = await ResolveAsync(options.Server, ct).ConfigureAwait(false);
            _serverIp = _serverEndpoint.Address.ToString();
            _socksEndpoint = ParseLocal(options.SocksListen);
            
            _client = await StartClientAsync(options, _serverEndpoint, _socksEndpoint, ct).ConfigureAwait(false);
            Info($"клиент поднят, SOCKS5 на {_client.SocksEndPoint}, несущих: {_client.CarrierCount}");
            
            var (gateway, ifIndex) = await _net.GetDefaultRouteAsync(ct).ConfigureAwait(false);
            _gatewayIp = gateway;
            _gatewayIfIndex = ifIndex;
            Info($"текущий шлюз: {gateway} (if {ifIndex})");
            await _net.AddHostRouteAsync(_serverIp, gateway, ifIndex, ct).ConfigureAwait(false);
            _hostRouteAdded = true;
            
            string device = OperatingSystem.IsWindows() ? options.TunDeviceName : $"tun://{options.TunDeviceName}";
            string proxy = $"socks5://{_socksEndpoint!.Address}:{_socksEndpoint.Port}";
            _tun2socks = ProcessRunner.Start(exe,
                $"--device {device} --proxy {proxy} --loglevel {options.Tun2SocksLogLevel}", m => Log?.Invoke(this, m),
                workDir);
            int tunIndex = await WaitForTunAsync(options.TunDeviceName, ct).ConfigureAwait(false);
            
            await _net.ConfigureTunAsync(options, tunIndex, ct).ConfigureAwait(false);
            await _net.AddDefaultViaTunAsync(options, ct).ConfigureAwait(false);
            _defaultRouteAdded = true;

            await ApplyBypassAsync(options, ct).ConfigureAwait(false);

            SetStatus(TunnelStatus.Connected);
            Info("VPN-режим включён: весь трафик идёт через туннель.");

            _watchdogCts = new CancellationTokenSource();
            _watchdog = Task.Run(() => WatchdogAsync(_watchdogCts.Token));
        }
        catch (Exception ex)
        {
            Info($"ошибка подключения: {ex.Message}");
            SetStatus(TunnelStatus.Error);
            await DisconnectAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        try
        {
            _watchdogCts?.Cancel();
        }
        catch
        {
            // ignored
        }

        if (_watchdog is not null)
        {
            try
            {
                await _watchdog.ConfigureAwait(false);
            }
            catch
            {
                // ignored
            }

            _watchdog = null;
        }

        _watchdogCts?.Dispose();
        _watchdogCts = null;

        await CleanupAsync(ct).ConfigureAwait(false);
        if (Status != TunnelStatus.Error) SetStatus(TunnelStatus.Disconnected);
    }

    private async Task ApplyBypassAsync(TunnelOptions options, CancellationToken ct)
    {
        if (options.BypassRules is null || _gatewayIp is null) return;
        int count = 0;
        foreach (var rule in options.BypassRules)
        {
            if (!rule.Enabled) continue;
            IReadOnlyList<(System.Net.IPAddress Network, int Prefix)> nets;
            try
            {
                nets = await rule.ResolveAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                continue;
            }

            foreach (var (net, prefix) in nets)
            {
                try
                {
                    await _net.AddBypassRouteAsync(net.ToString(), prefix, _gatewayIp, _gatewayIfIndex, ct)
                        .ConfigureAwait(false);
                    _bypassAdded.Add((net.ToString(), prefix));
                    count++;
                }
                catch (Exception e)
                {
                    Info($"bypass {rule.Value}: {e.Message}");
                }
            }
        }

        if (count > 0) Info($"split-tunnel: {count} адрес(ов) идут напрямую мимо туннеля");
    }

    /// <summary>Снимает маршруты, гасит tun2socks и клиента. Идемпотентно.</summary>
    private async Task CleanupAsync(CancellationToken ct)
    {
        foreach (var (net, prefix) in _bypassAdded)
        {
            try
            {
                await _net.RemoveBypassRouteAsync(net, prefix, ct).ConfigureAwait(false);
            }
            catch
            {
                // ignored
            }
        }

        _bypassAdded.Clear();

        if (_options is { } o)
        {
            if (_defaultRouteAdded)
            {
                try
                {
                    await _net.RemoveDefaultViaTunAsync(o, ct).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    Info(e.Message);
                }

                _defaultRouteAdded = false;
            }

            if (_hostRouteAdded && _serverIp is not null)
            {
                try
                {
                    await _net.RemoveHostRouteAsync(_serverIp, ct).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    Info(e.Message);
                }

                _hostRouteAdded = false;
            }
        }

        if (_tun2socks is { } p)
        {
            try
            {
                if (!p.HasExited) p.Kill(true);
            }
            catch
            {
                // ignored
            }

            p.Dispose();
            _tun2socks = null;
        }

        if (_client is { } c)
        {
            try
            {
                await c.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // ignored
            }

            _client = null;
        }
    }

    private async Task<ChameleonClient> StartClientAsync(TunnelOptions options, IPEndPoint serverEndpoint,
        IPEndPoint socksEndpoint, CancellationToken ct)
    {
        IPEndPoint[]? extras = options.ExtraCarriers is { Length: > 0 }
            ? await Task.WhenAll(options.ExtraCarriers
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(e => ResolveAsync(e, ct))).ConfigureAwait(false)
            : null;
        KeyPair clientKey = options.ClientPrivateKeyHex is { Length: > 0 } hex
            ? new KeyPair(Convert.FromHexString(hex), X25519.ScalarMultBase(Convert.FromHexString(hex)))
            : X25519.GenerateKeyPair();
        return await ChameleonClient.StartAsync(
                serverEndpoint, clientKey, Convert.FromHexString(options.ServerPublicKeyHex),
                socksEndpoint, carrier: BcTlsCarrier.Client(options.Sni), extraCarrierEndpoints: extras,
                cancellationToken: ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Следит за обрывом сессии. При обрыве пытается переподнять клиента (маршруты и
    /// tun2socks остаются - короткие обрывы переживаются бесшовно). Если не удалось за
    /// заданное число попыток - откатывает маршруты, чтобы вернуть прямой интернет.
    /// </summary>
    private async Task WatchdogAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var client = _client;
            if (client is null) return;

            try
            {
                await client.Completion.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (ct.IsCancellationRequested) return;

            Info("сессия оборвалась - пробую переподключиться…");
            SetStatus(TunnelStatus.Reconnecting);

            bool ok = false;
            for (int attempt = 1;
                 attempt <= (_options?.ReconnectAttempts ?? 5) && !ct.IsCancellationRequested;
                 attempt++)
            {
                try
                {
                    if (_client is { } old)
                    {
                        try
                        {
                            await old.DisposeAsync().ConfigureAwait(false);
                        }
                        catch
                        {
                            // ignored
                        }
                    }

                    _client = await StartClientAsync(_options!, _serverEndpoint!, _socksEndpoint!, ct)
                        .ConfigureAwait(false);
                    Info($"переподключено (попытка {attempt}), несущих: {_client.CarrierCount}");
                    SetStatus(TunnelStatus.Connected);
                    ok = true;
                    break;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception e)
                {
                    Info($"попытка {attempt} не удалась: {e.Message}");
                    try
                    {
                        await Task.Delay(_options?.ReconnectDelayMs ?? 3000, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            }

            if (!ok)
            {
                Info("переподключиться не удалось - откатываю маршруты (возвращаю прямой интернет)");
                await CleanupAsync(CancellationToken.None).ConfigureAwait(false);
                SetStatus(TunnelStatus.Error);
                return;
            }
        }
    }

    /// <summary>Ждёт появления TUN-адаптера И его готовности (Up). Возвращает индекс интерфейса.</summary>
    private async Task<int> WaitForTunAsync(string name, CancellationToken ct)
    {
        bool requireUp = OperatingSystem.IsWindows();
        for (int i = 0; i < 100; i++) // до ~10 c
        {
            var ni = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => n.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            bool ready = ni is not null &&
                         (!requireUp || ni.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up);
            if (ready)
            {
                int idx = 0;
                try
                {
                    idx = ni!.GetIPProperties().GetIPv4Properties().Index;
                }
                catch
                {
                    // ignored
                }

                Info($"TUN-адаптер «{name}» обнаружен (if {idx})");
                return idx;
            }

            if (_tun2socks?.HasExited == true)
                throw new InvalidOperationException("tun2socks завершился преждевременно");
            await Task.Delay(100, ct).ConfigureAwait(false);
        }

        var names = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
            .Select(n => $"{n.Name}[{n.OperationalStatus}]");
        throw new InvalidOperationException(
            $"TUN-адаптер «{name}» не поднялся за 10 с. Доступные: {string.Join(", ", names)}. " +
            "Если tun2socks назвал адаптер иначе - задайте имя в TunnelOptions.TunDeviceName.");
    }

    private void Info(string m) => Log?.Invoke(this, m);

    private void SetStatus(TunnelStatus s)
    {
        Status = s;
        StatusChanged?.Invoke(this, s);
    }

    /// <summary>Находит tun2socks: по указанному пути или в PATH. Иначе - понятная ошибка.</summary>
    private static string ResolveExecutable(string path)
    {
        if (File.Exists(path)) return Path.GetFullPath(path);

        bool bareName = !path.Contains(Path.DirectorySeparatorChar) && !path.Contains('/');
        if (bareName)
        {
            string[] dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
            foreach (string dir in dirs)
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                string candidate = Path.Combine(dir.Trim(), path);
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
                if (OperatingSystem.IsWindows() && !candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    string withExe = candidate + ".exe";
                    if (File.Exists(withExe)) return Path.GetFullPath(withExe);
                }
            }
        }

        throw new FileNotFoundException(
            $"Не найден tun2socks: «{path}». Укажите путь через --tun2socks (или переменную CHAMELEON_TUN2SOCKS), " +
            "либо положите tun2socks.exe в каталог из PATH. Рядом с tun2socks.exe должна лежать wintun.dll той же разрядности.");
    }

    private static async Task<IPEndPoint> ResolveAsync(string hostPort, CancellationToken ct)
    {
        int i = hostPort.LastIndexOf(':');
        string host = hostPort[..i];
        int port = int.Parse(hostPort[(i + 1)..]);
        if (IPAddress.TryParse(host, out var ip)) return new IPEndPoint(ip, port);
        var addrs = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        return new IPEndPoint(addrs.First(a => a.AddressFamily == AddressFamily.InterNetwork), port);
    }

    private static IPEndPoint ParseLocal(string s)
    {
        int i = s.LastIndexOf(':');
        return new IPEndPoint(IPAddress.Parse(s[..i]), int.Parse(s[(i + 1)..]));
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync().ConfigureAwait(false);
}