using System;
using System.Collections.Generic;
using System.IO;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Graphics.Drawables;
using Chameleon.Mobile;

namespace Chameleon.Mobile.Android;

/// <summary>
/// Список установленных приложений через PackageManager (для маршрутизации
/// по приложениям). Берём только приложения с разрешением INTERNET - остальным
/// VPN всё равно без разницы. Иконка отдаётся как PNG-байты, UI сам делает Bitmap.
///
/// ВАЖНО: чтобы видеть все приложения на Android 11+ нужен в манифесте
///   &lt;uses-permission android:name="android.permission.QUERY_ALL_PACKAGES"/&gt;
/// </summary>
public sealed class AndroidAppList(Context ctx) : IAppList
{
    public IReadOnlyList<AppEntry> GetInstalled()
    {
        var pm = ctx.PackageManager;
        var result = new List<AppEntry>();
        if (pm is null) return result;

        string self = ctx.PackageName ?? "";

        IList<ApplicationInfo> apps;
        try
        {
            apps = pm.GetInstalledApplications(PackageInfoFlags.MetaData);
        }
        catch
        {
            return result;
        }

        foreach (var ai in apps)
        {
            string pkg = ai.PackageName ?? "";
            if (pkg.Length == 0 || pkg == self) continue;
            
            try
            {
                if (pm.CheckPermission(global::Android.Manifest.Permission.Internet, pkg) != Permission.Granted)
                    continue;
            }
            catch
            {
                // ignored
            }

            string label;
            try
            {
                label = pm.GetApplicationLabel(ai) ?? pkg;
            }
            catch
            {
                label = pkg;
            }

            byte[]? icon = null;
            try
            {
                icon = DrawableToPng(pm.GetApplicationIcon(pkg), 96);
            }
            catch
            {
                // ignored
            }

            result.Add(new AppEntry(pkg, label, icon));
        }

        result.Sort((a, b) => string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    private static byte[]? DrawableToPng(Drawable? d, int size)
    {
        if (d is null) return null;
        var cfg = Bitmap.Config.Argb8888;
        if (cfg is null) return null;

        using var bmp = Bitmap.CreateBitmap(size, size, cfg);
        if (bmp is null) return null;
        var canvas = new Canvas(bmp);
        d.SetBounds(0, 0, size, size);
        d.Draw(canvas);

        using var ms = new MemoryStream();
        var png = Bitmap.CompressFormat.Png;
        if (png is null) return null;
        bmp.Compress(png, 100, ms);
        bmp.Recycle();
        return ms.ToArray();
    }
}