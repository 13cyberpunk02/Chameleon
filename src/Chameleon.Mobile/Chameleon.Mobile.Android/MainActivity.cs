using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Avalonia.Android;
using Chameleon.Mobile.Android.Vpn;

namespace Chameleon.Mobile.Android;

[Activity(
    Label = "Chameleon",
    Theme = "@style/MyTheme.NoActionBar",
    Icon = "@mipmap/appicon",
    RoundIcon = "@mipmap/appicon_round",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public class MainActivity : AvaloniaMainActivity, IVpnController
{
    private string? _pendingLink;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Chameleon.Mobile.Vpn.Current = this;
        Chameleon.Mobile.Vpn.Clipboard = new AndroidClipboard();
        Chameleon.Mobile.Vpn.Apps = new AndroidAppList(this);
    }

    // При возврате из фона - снова регистрируем себя (Activity могла пересоздаться).
    protected override void OnResume()
    {
        base.OnResume();
        Chameleon.Mobile.Vpn.Current = this;
    }

    public void Connect(string link)
    {
        _pendingLink = link;
        var prepare = VpnController.Prepare(this);
        if (prepare is not null)
            StartActivityForResult(prepare, VpnController.RequestCode);
        else
            StartVpn();
    }
    
    public void Disconnect() => VpnController.Stop();

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode == VpnController.RequestCode)
        {
            if (resultCode == Result.Ok) StartVpn();
            else Chameleon.Mobile.Vpn.Report(VpnStatus.Disconnected);
        }
    }

    private void StartVpn()
    {
        if (!string.IsNullOrEmpty(_pendingLink))
            VpnController.Start(this, _pendingLink);
    }
}
