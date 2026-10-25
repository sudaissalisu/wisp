using Android.App;
using Android.Appwidget;
using Android.Content;
using Android.Views;
using Android.Widget;

namespace Wisp.Android;

/// <summary>
/// Home-screen widget for Wisp: device name, nearby/connection state, live
/// transfer progress, and a one-tap Send shortcut.
/// </summary>
[BroadcastReceiver(
    Exported = true,
    Label = "Wisp status")]
[IntentFilter(new[] { AppWidgetManager.ActionAppwidgetUpdate })]
[MetaData("android.appwidget.provider", Resource = "@xml/wisp_widget_info")]
public sealed class WispWidgetProvider : AppWidgetProvider
{
    private const string ExtraMode = "mode";
    private const string ExtraTitle = "title";
    private const string ExtraStatus = "status";
    private const string ExtraProgress = "progress";

    public const string ActionQuickSend = "com.wisp.app.QUICK_SEND";

    public override void OnUpdate(Context context, AppWidgetManager manager, int[] appWidgetIds)
    {
        Update(context, "idle", "Ready nearby", "No transfers in progress", -1);
    }

    public override void OnReceive(Context context, Intent intent)
    {
        base.OnReceive(context, intent);
        var mode = intent.GetStringExtra(ExtraMode);
        if (mode is null) return;
        Update(context,
            mode,
            intent.GetStringExtra(ExtraTitle) ?? "Wisp",
            intent.GetStringExtra(ExtraStatus) ?? "",
            intent.GetIntExtra(ExtraProgress, -1));
    }

    public static void Update(Context context, string mode, string title, string status, int progress)
    {
        try
        {
            var manager = AppWidgetManager.GetInstance(context);
            if (manager is null) return;
            var ids = manager.GetAppWidgetIds(
                new ComponentName(context, Java.Lang.Class.FromType(typeof(WispWidgetProvider))));
            if (ids is null || ids.Length == 0) return;

            var views = new RemoteViews(context.PackageName, Resource.Layout.wisp_widget);
            views.SetTextViewText(Resource.Id.widget_title, title);
            views.SetTextViewText(Resource.Id.widget_status, status);

            if (progress >= 0)
            {
                views.SetViewVisibility(Resource.Id.widget_progress, ViewStates.Visible);
                views.SetProgressBar(Resource.Id.widget_progress, 100,
                    Math.Clamp(progress, 0, 100), false);
            }
            else
            {
                views.SetViewVisibility(Resource.Id.widget_progress, ViewStates.Gone);
            }

            // Tapping anywhere opens Wisp; the Send pill also starts the picker.
            var open = new Intent(context, typeof(MainActivity));
            open.SetFlags(ActivityFlags.NewTask | ActivityFlags.SingleTop);
            open.PutExtra("wisp_action", "open");
            views.SetOnClickPendingIntent(Resource.Id.widget_root,
                PendingIntent.GetActivity(context, 4102, open,
                    PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable));

            var send = new Intent(context, typeof(MainActivity));
            send.SetFlags(ActivityFlags.NewTask | ActivityFlags.SingleTop);
            send.PutExtra("wisp_action", "send");
            views.SetOnClickPendingIntent(Resource.Id.widget_send,
                PendingIntent.GetActivity(context, 4103, send,
                    PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable));

            manager.UpdateAppWidget(ids, views);
        }
        catch
        {
        }
    }
}
