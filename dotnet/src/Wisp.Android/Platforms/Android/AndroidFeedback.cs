using Android.OS;
using Android.Widget;
using AndroidX.Core.App;
using AndroidX.Core.Content;
using Google.Android.Material.Snackbar;
using AndroidApplication = Android.App.Application;

namespace Wisp.Android;

/// <summary>
/// Bottom-of-screen feedback using Android's Material Snackbar (the system
/// bottom toast), with a plain Toast fallback when no window is available.
/// </summary>
public static class AndroidFeedback
{
    private const int SnackbarShort = -1;
    private const int SnackbarLong = 0;
    private const string LocalNetworkPermission = "android.permission.ACCESS_LOCAL_NETWORK";

    public static void Show(string message, bool error = false, bool longDuration = false)
    {
        try
        {
            var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
            var content = activity?.Window?.DecorView?
                .FindViewById(global::Android.Resource.Id.Content);
            if (activity is null || content is null)
            {
                ShowToast(message);
                return;
            }

            var snackbar = Snackbar.Make(content, message,
                longDuration ? SnackbarLong : SnackbarShort);
            snackbar.SetBackgroundTint(global::Android.Graphics.Color.ParseColor(
                error ? "#B33A2E" : "#1F2A26"));
            var view = snackbar.View;
            var text = view.FindViewById<TextView>(global::Android.Resource.Id.Text1)
                       ?? view.FindViewById<TextView>(Resource.Id.snackbar_text);
            if (text is not null)
            {
                text.SetTextColor(global::Android.Graphics.Color.White);
                text.SetMaxLines(3);
            }
            view.Background?.SetTintList(global::Android.Content.Res.ColorStateList.ValueOf(
                error
                    ? global::Android.Graphics.Color.ParseColor("#B33A2E")
                    : global::Android.Graphics.Color.ParseColor("#1F2A26")));
            snackbar.Show();
        }
        catch
        {
            ShowToast(message);
        }
    }

    private static void ShowToast(string message)
    {
        void Show()
        {
            try
            {
                var toast = Toast.MakeText(AndroidApplication.Context, message, ToastLength.Long);
                toast?.Show();
            }
            catch
            {
            }
        }
        if (MainThread.IsMainThread) Show();
        else MainThread.BeginInvokeOnMainThread(Show);
    }
}

/// <summary>
/// Requests every runtime permission Wisp needs, in one place, the first time
/// the app runs: notifications, nearby Wi-Fi devices, and (on Android 16+) the
/// local-network permission that gates LAN sockets.
/// </summary>
public static class AndroidPermissions
{
    private const int RequestCode = 7301;
    private const string LocalNetworkPermission = "android.permission.ACCESS_LOCAL_NETWORK";
    private static bool _requested;

    public static void RequestAll(bool force = false)
    {
        try
        {
            if (force) _requested = false;
            var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
            if (activity is null || _requested) return;
            _requested = true;

            var permissions = new List<string>();
            void AddIfNeeded(string permission, int minSdk)
            {
                if ((int)Build.VERSION.SdkInt < minSdk) return;
                if (ContextCompat.CheckSelfPermission(activity, permission)
                    == global::Android.Content.PM.Permission.Granted) return;
                permissions.Add(permission);
            }

            AddIfNeeded(global::Android.Manifest.Permission.PostNotifications, 33);
            AddIfNeeded(global::Android.Manifest.Permission.NearbyWifiDevices, 33);
            AddIfNeeded(LocalNetworkPermission, 36);

            if (permissions.Count == 0) return;
            ActivityCompat.RequestPermissions(activity, permissions.ToArray(), RequestCode);
        }
        catch
        {
        }
    }
}
