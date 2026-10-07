using Android.App;
using Android.Content;
using Android.Net;

namespace Chameleon.Mobile.Android.Vpn;

/// <summary>
/// Помощник для UI: запрос разрешения VPN и запуск/остановка сервиса.
/// Разрешение запрашивается через VpnService.Prepare() - если вернул Intent,
/// показываем системный диалог (StartActivityForResult); по OK стартуем сервис.
/// </summary>
public static class VpnController
{
    public const int RequestCode = 0x7A11;

    /// <summary>
    /// Готовит VPN: если нужно разрешение - вернёт Intent (его надо запустить через
    /// activity.StartActivityForResult(intent, RequestCode)). Если уже разрешено - null.
    /// </summary>
    public static Intent? Prepare(Context context) => VpnService.Prepare(context);

    /// <summary>Запустить VPN-сервис (вызывать, когда разрешение уже есть).</summary>
    public static void Start(Context context, string? link = null)
    {
        var intent = new Intent(context, typeof(ChameleonVpnService))
            .SetAction(ChameleonVpnService.ActionStart);
        if (!string.IsNullOrEmpty(link)) intent.PutExtra(ChameleonVpnService.ExtraLink, link);
        if (global::Android.OS.Build.VERSION.SdkInt >= global::Android.OS.BuildVersionCodes.O)
            context.StartForegroundService(intent);
        else
            context.StartService(intent);
    }

    public static void Stop(Context context)
    {
        var intent = new Intent(context, typeof(ChameleonVpnService))
            .SetAction(ChameleonVpnService.ActionStop);
        context.StartService(intent);
    }
}