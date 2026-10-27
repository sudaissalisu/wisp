using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;

namespace Wisp.Android;

[Activity(
    Theme = "@style/Maui.SplashTheme",
    MainLauncher = true,
    LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation |
                           ConfigChanges.UiMode | ConfigChanges.ScreenLayout |
                           ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
[IntentFilter(
    new[] { Intent.ActionSend },
    Categories = new[] { Intent.CategoryDefault },
    DataMimeType = "*/*")]
[IntentFilter(
    new[] { Intent.ActionSendMultiple },
    Categories = new[] { Intent.CategoryDefault },
    DataMimeType = "*/*")]
public class MainActivity : MauiAppCompatActivity
{
    private BackPressCallback? _backPressCallback;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        // On .NET 10 MAUI the root page's OnBackButtonPressed override is not
        // enough: back can finish the activity before the page sees it. Register
        // an AndroidX callback so Wisp can close a sheet or step back through
        // tabs, and fall through to the system only when nothing is left.
        _backPressCallback = new BackPressCallback(this);
        OnBackPressedDispatcher.AddCallback(this, _backPressCallback);

        ShareInbox.HandleIntent(Intent);
        AppActionInbox.HandleIntent(Intent);
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        ShareInbox.HandleIntent(intent);
        AppActionInbox.HandleIntent(intent);
    }

    private sealed class BackPressCallback : AndroidX.Activity.OnBackPressedCallback
    {
        private readonly MainActivity _activity;

        public BackPressCallback(MainActivity activity) : base(true)
        {
            _activity = activity;
        }

        public override void HandleOnBackPressed()
        {
            var page = Microsoft.Maui.Controls.Application.Current?.Windows
                .FirstOrDefault()?.Page as MainPage;
            if (page?.HandleBackNavigation() == true) return;

            // Nothing left in Wisp to close: let the system handle back.
            Enabled = false;
            try
            {
                _activity.OnBackPressedDispatcher.OnBackPressed();
            }
            finally
            {
                Enabled = true;
            }
        }
    }
}

/// <summary>
/// Receives content:// URIs from Android's share sheet and holds them until the
/// Wisp page is ready to stage and send them.
/// </summary>
public static class ShareInbox
{
    private static readonly object Gate = new();
    private static readonly List<global::Android.Net.Uri> Pending = [];

    public static event Action? Changed;

    public static void HandleIntent(Intent? intent)
    {
        if (intent is null) return;
        if (intent.Action != Intent.ActionSend && intent.Action != Intent.ActionSendMultiple) return;

        var uris = new List<global::Android.Net.Uri>();
        if (intent.Action == Intent.ActionSend)
        {
            if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
            {
                var uri = intent.GetParcelableExtra(
                    Intent.ExtraStream,
                    Java.Lang.Class.FromType(typeof(global::Android.Net.Uri))) as global::Android.Net.Uri;
                if (uri is not null) uris.Add(uri);
            }
            else
            {
#pragma warning disable CA1422
                if (intent.GetParcelableExtra(Intent.ExtraStream) is global::Android.Net.Uri uri) uris.Add(uri);
#pragma warning restore CA1422
            }
        }
        else
        {
            var list = intent.GetParcelableArrayListExtra(Intent.ExtraStream);
            if (list is not null)
            {
                foreach (var item in list)
                {
                    if (item is global::Android.Net.Uri uri) uris.Add(uri);
                }
            }
        }

        // Many galleries put the URI in ClipData rather than EXTRA_STREAM.
        if (uris.Count == 0 && intent.ClipData is { ItemCount: > 0 } clip)
        {
            for (var i = 0; i < clip.ItemCount; i++)
            {
                var uri = clip.GetItemAt(i)?.Uri;
                if (uri is not null) uris.Add(uri);
            }
        }
        if (uris.Count == 0 && intent.Data is { } dataUri) uris.Add(dataUri);
        if (uris.Count == 0) return;
        lock (Gate)
        {
            for (var i = 0; i < uris.Count; i++) Pending.Add(uris[i]);
        }
        MainThread.BeginInvokeOnMainThread(() => Changed?.Invoke());
    }

    public static IReadOnlyList<global::Android.Net.Uri> Drain()
    {
        lock (Gate)
        {
            var items = Pending.ToArray();
            Pending.Clear();
            return items;
        }
    }
}


/// <summary>
/// Carries lightweight app actions from the widget or notification into the
/// running page (currently the quick-send file picker).
/// </summary>
public static class AppActionInbox
{
    private static int _pendingSend;

    public static event Action? Changed;

    public static void HandleIntent(Intent? intent)
    {
        if (intent?.GetStringExtra("wisp_action") is not { } action) return;
        if (!action.Equals("send", StringComparison.OrdinalIgnoreCase)) return;
        Interlocked.Exchange(ref _pendingSend, 1);
        MainThread.BeginInvokeOnMainThread(() => Changed?.Invoke());
    }

    public static bool DrainSend() => Interlocked.Exchange(ref _pendingSend, 0) == 1;
}
