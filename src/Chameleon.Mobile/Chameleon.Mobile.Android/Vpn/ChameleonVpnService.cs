using System;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.OS;
using Android.Net;
using Chameleon.Mobile;

namespace Chameleon.Mobile.Android.Vpn;

/// <summary>
/// Android VPN-сервис. Поднимает TUN через системный VpnService + foreground-
/// уведомление (иначе система прибьёт сервис). Этап 1: только TUN + fd, без
/// tun2socks/Core (трафик пойдёт на этапе 3).
///
/// Без AndroidX - нативный Notification.Builder.
/// </summary>
[Service(Permission = "android.permission.BIND_VPN_SERVICE", Exported = false,
    ForegroundServiceType = global::Android.Content.PM.ForegroundService.TypeSystemExempted)]
[IntentFilter(["android.net.VpnService"])]
public sealed class ChameleonVpnService : VpnService
{
    public const string ActionStart = "chameleon.vpn.START";
    public const string ActionStop = "chameleon.vpn.STOP";
    private const string ChannelId = "chameleon_vpn";
    private const int NotifId = 1001;

    private ParcelFileDescriptor? _tun;
    private VpnEngine? _engine;
    private Thread? _tun2socks;
    private int _tunFd = -1;
    private System.Threading.Timer? _statsTimer;
    private CancellationTokenSource? _cts;

    public const string ExtraLink = "chameleon.link";
    
    private const string TestLink = "chameleon://CHANGE_ME:443?key=...&sni=...&ck=...";

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (intent?.Action == ActionStop)
        {
            Stop();
            return StartCommandResult.NotSticky;
        }

        string link = intent?.GetStringExtra(ExtraLink) is { Length: > 0 } l ? l : TestLink;

        global::Chameleon.Mobile.Vpn.Report(VpnStatus.Connecting);
        StartForegroundInternal();
        try
        {
            EstablishTun();
        }
        catch (Exception e)
        {
            global::Android.Util.Log.Error("ChameleonVpn", "establish failed: " + e);
            global::Chameleon.Mobile.Vpn.Report(VpnStatus.Error);
            Stop();
            return StartCommandResult.NotSticky;
        }
        
        int fdBefore = _tun!.Fd;
        int tunFd = _tun.DetachFd();
        _tunFd = tunFd;
        global::Android.Util.Log.Info("ChameleonVpn", $"fd: before(getFd)={fdBefore}, detached={tunFd}");
        _cts = new CancellationTokenSource();
        _engine = new VpnEngine();
        _ = Task.Run(async () =>
        {
            try
            {
                await _engine.StartAsync(link, _cts.Token);
                _tun2socks = new Thread(() =>
                {
                    string hevLog = System.IO.Path.Combine(FilesDir!.AbsolutePath, "hev.log");
                    global::Android.Util.Log.Info("ChameleonVpn", $"hev log → {hevLog}");
                    int rc = Tun2Socks.Run(tunFd, "127.0.0.1", VpnEngine.SocksPort, mtu: 8500, logFile: hevLog);
                    global::Android.Util.Log.Info("ChameleonVpn", $"tun2socks завершился, rc={rc}");
                }) { IsBackground = true, Name = "tun2socks" };
                _tun2socks.Start();
                global::Android.Util.Log.Info("ChameleonVpn",
                    $"tun2socks запущен (fd={tunFd} → SOCKS {VpnEngine.SocksPort})");
                global::Chameleon.Mobile.Vpn.Report(VpnStatus.Connected);
                _ = Task.Run(FetchIpAsync); // внешний IP через наш SOCKS
                _statsTimer = new System.Threading.Timer(_ =>
                {
                    try
                    {
                        if (_engine is { } en)
                            global::Chameleon.Mobile.Vpn.ReportStats(
                                new global::Chameleon.Mobile.VpnStats(en.BytesUp, en.BytesDown, en.RttMs));
                    }
                    catch
                    {
                        // ignored
                    }
                }, null, 1000, 2000);
            }
            catch (Exception e)
            {
                global::Android.Util.Log.Error("ChameleonVpn", "start failed: " + e);
                global::Chameleon.Mobile.Vpn.Report(VpnStatus.Error);
            }
        });

        return StartCommandResult.Sticky;
    }

    private void EstablishTun()
    {
        var builder = new Builder(this)
            .SetSession("Chameleon")
            .SetMtu(8500)
            .AddAddress("10.0.0.2", 24)
            .AddDnsServer("1.1.1.1")
            .AddDnsServer("9.9.9.9")
            .AddDnsServer("8.8.8.8")
            .AddRoute("0.0.0.0", 0);
        
        try
        {
            if (PackageName is not null) builder.AddDisallowedApplication(PackageName);
        }
        catch
        {
            // ignored
        }

        _tun = builder.Establish();
        if (_tun is null)
            throw new InvalidOperationException("Establish() == null (нет разрешения VPN?)");

        global::Android.Util.Log.Info("ChameleonVpn", $"TUN up, fd={_tun.Fd}");
    }

    /// <summary>Получить внешний IP через наш SOCKS (как на десктопе) и сообщить в UI.</summary>
    private static async Task FetchIpAsync()
    {
        try
        {
            var handler = new System.Net.Http.SocketsHttpHandler
            {
                Proxy = new System.Net.WebProxy($"socks5://127.0.0.1:{VpnEngine.SocksPort}"),
                UseProxy = true,
            };
            using var http = new System.Net.Http.HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
            string ip = (await http.GetStringAsync("https://api.ipify.org").ConfigureAwait(false)).Trim();
            global::Chameleon.Mobile.Vpn.ReportIp(string.IsNullOrWhiteSpace(ip) ? "-" : ip);
        }
        catch
        {
            global::Chameleon.Mobile.Vpn.ReportIp("-");
        }
    }

    private volatile bool _stopping;

    private void Stop()
    {
        if (_stopping) return;
        _stopping = true;

        try
        {
            _statsTimer?.Dispose();
        }
        catch
        {
            // ignored
        }

        _statsTimer = null;

        try
        {
            Tun2Socks.Stop();
        }
        catch
        {
            // ignored
        }

        if (_tunFd >= 0)
        {
            try
            {
                Tun2Socks.CloseFd(_tunFd);
            }
            catch
            {
                // ignored
            }

            _tunFd = -1;
        }

        try
        {
            _tun2socks?.Join(1500);
        }
        catch
        {
            // ignored
        }

        _tun2socks = null;

        try
        {
            _cts?.Cancel();
        }
        catch
        {
            // ignored
        }

        if (_engine is not null)
        {
            var e = _engine;
            _engine = null;
            _ = Task.Run(async () =>
            {
                try
                {
                    await e.DisposeAsync();
                }
                catch
                {
                    // ignored
                }
            });
        }

        _tun = null;
        
        try
        {
            StopForeground(StopForegroundFlags.Remove);
        }
        catch
        {
            // ignored
        }

        try
        {
            StopSelf();
        }
        catch
        {
            // ignored
        }

        try
        {
            global::Chameleon.Mobile.Vpn.Report(VpnStatus.Disconnected);
        }
        catch
        {
            // ignored
        }
    }

    private void StartForegroundInternal()
    {
        Notification notif;
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            var mgr = (NotificationManager)GetSystemService(NotificationService)!;
            if (mgr.GetNotificationChannel(ChannelId) is null)
                mgr.CreateNotificationChannel(new NotificationChannel(
                    ChannelId, "Chameleon VPN", NotificationImportance.Low));
            notif = new Notification.Builder(this, ChannelId)
                .SetContentTitle("Chameleon")
                .SetContentText("VPN активен")
                .SetSmallIcon(global::Android.Resource.Drawable.IcLockLock)
                .SetOngoing(true)
                .Build();
        }
        else
        {
#pragma warning disable CS0618
            notif = new Notification.Builder(this)
                .SetContentTitle("Chameleon")
                .SetContentText("VPN активен")
                .SetSmallIcon(global::Android.Resource.Drawable.IcLockLock)
                .SetOngoing(true)
                .Build();
#pragma warning restore CS0618
        }

        if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
            StartForeground(NotifId, notif,
                global::Android.Content.PM.ForegroundService.TypeSystemExempted);
        else
            StartForeground(NotifId, notif);
    }

    public override void OnRevoke()
    {
        Stop();
        base.OnRevoke();
    }

    public override void OnDestroy()
    {
        try
        {
            _tun?.Close();
        }
        catch
        {
            // ignored
        }

        base.OnDestroy();
    }
}