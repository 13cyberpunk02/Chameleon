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
/// Без AndroidX — нативный Notification.Builder.
/// </summary>
[Service(Permission = "android.permission.BIND_VPN_SERVICE", Exported = false,
    ForegroundServiceType = global::Android.Content.PM.ForegroundService.TypeSystemExempted)]
[IntentFilter(new[] { "android.net.VpnService" })]
public sealed class ChameleonVpnService : VpnService
{
    public const string ActionStart = "chameleon.vpn.START";
    public const string ActionStop = "chameleon.vpn.STOP";
    private const string ChannelId = "chameleon_vpn";
    private const int NotifId = 1001;

    private ParcelFileDescriptor? _tun;
    private VpnEngine? _engine;
    private Thread? _tun2socks;
    private CancellationTokenSource? _cts;

    public const string ExtraLink = "chameleon.link";

    // ВРЕМЕННО для теста: вставь СВОЮ ссылку chameleon:// из панели (или передавай через ExtraLink).
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

        // Этап 2: поднять ChameleonClient (SOCKS) из ссылки. Трафик пойдёт на этапе 3
        // (tun2socks свяжет TUN fd с этим SOCKS).
        int fdBefore = _tun!.Fd; // fd, пока PFD им владеет
        int tunFd = _tun.DetachFd(); // отсоединённый fd для hev
        global::Android.Util.Log.Info("ChameleonVpn", $"fd: before(getFd)={fdBefore}, detached={tunFd}");
        _cts = new CancellationTokenSource();
        _engine = new VpnEngine();
        _ = Task.Run(async () =>
        {
            try
            {
                await _engine.StartAsync(link, _cts.Token); // поднять SOCKS (ChameleonClient)
                // SOCKS готов → запускаем tun2socks (БЛОКИРУЮЩИЙ, свой поток).
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
            .SetMtu(8500) // как в примерах hev-socks5-tunnel
            .AddAddress("10.0.0.2", 24) // адрес с подсетью (не /32)
            .AddDnsServer("1.1.1.1")
            .AddDnsServer("8.8.8.8")
            .AddRoute("0.0.0.0", 0); // весь IPv4 в туннель

        // своё приложение — мимо туннеля (иначе петля)
        try
        {
            if (PackageName is not null) builder.AddDisallowedApplication(PackageName);
        }
        catch
        {
        }

        _tun = builder.Establish();
        if (_tun is null)
            throw new InvalidOperationException("Establish() == null (нет разрешения VPN?)");

        global::Android.Util.Log.Info("ChameleonVpn", $"TUN up, fd={_tun.Fd}");
        // TODO этап 3: tun2socks(fd, socksAddr) + ChameleonClient
    }

    private volatile bool _stopping;

    private void Stop()
    {
        if (_stopping) return;
        _stopping = true;

        // Статус — сразу (UI отреагирует), остальное максимально безопасно.
        try
        {
            global::Chameleon.Mobile.Vpn.Report(VpnStatus.Disconnected);
        }
        catch
        {
        }

        // Остановить нативный туннель (hev сам закроет fd, которым владеет).
        try
        {
            Tun2Socks.Stop();
        }
        catch
        {
        }

        // Остановить наш клиент.
        try
        {
            _cts?.Cancel();
        }
        catch
        {
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
                }
            });
        }

        // НЕ трогаем _tun.Fd — он отсоединён (DetachFd) и принадлежит hev.
        _tun = null;

        try
        {
            StopForeground(StopForegroundFlags.Remove);
        }
        catch
        {
        }

        try
        {
            StopSelf();
        }
        catch
        {
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
        }

        base.OnDestroy();
    }
}