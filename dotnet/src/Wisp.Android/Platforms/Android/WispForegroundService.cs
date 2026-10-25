using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.App;
using AndroidX.Core.Content;

namespace Wisp.Android;

/// <summary>
/// Keeps the process alive and visible while a transfer is active, publishes a
/// live progress notification, and posts separate success/failure alerts.
/// </summary>
[Service(
    Exported = false,
    ForegroundServiceType = ForegroundService.TypeDataSync)]
public sealed class WispForegroundService : Service
{
    private const string ChannelTransfer = "wisp_transfer";
    private const string ChannelResult = "wisp_result";
    private const int TransferNotificationId = 73;
    private const string ActionStart = "com.wisp.app.START_TRANSFER";
    private const string ActionStop = "com.wisp.app.STOP_TRANSFER";
    private const string ExtraText = "text";

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        CreateChannels();
        if (intent?.Action == ActionStop)
        {
            StopForeground(StopForegroundFlags.Remove);
            StopSelf();
            return StartCommandResult.NotSticky;
        }

        var text = intent?.GetStringExtra(ExtraText) ?? "Transferring files securely";
        var notification = new NotificationCompat.Builder(this, ChannelTransfer)
            .SetSmallIcon(Resource.Drawable.wisp_notification)
            .SetContentTitle("Wisp")
            .SetContentText(text)
            .SetOngoing(true)
            .SetOnlyAlertOnce(true)
            .SetColor(global::Android.Graphics.Color.ParseColor("#45E3B4"))
            .SetCategory(NotificationCompat.CategoryProgress)
            .SetVisibility(NotificationCompat.VisibilityPrivate)
            .SetContentIntent(LaunchPendingIntent(this))
            .Build();
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
            StartForeground(TransferNotificationId, notification, ForegroundService.TypeDataSync);
        else
            StartForeground(TransferNotificationId, notification);
        return StartCommandResult.NotSticky;
    }

    public static void Start(Context context, string text)
    {
        var intent = new Intent(context, typeof(WispForegroundService));
        intent.SetAction(ActionStart);
        intent.PutExtra(ExtraText, text);
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
            context.StartForegroundService(intent);
        else
            context.StartService(intent);
    }

    public static void Stop(Context context)
    {
        var intent = new Intent(context, typeof(WispForegroundService));
        intent.SetAction(ActionStop);
        context.StopService(intent);
    }

    /// <summary>Updates the ongoing transfer notification with progress.</summary>
    public static void UpdateProgress(Context context, int percent, string text)
    {
        if (!CanPostNotifications(context)) return;
        try
        {
            var notification = new NotificationCompat.Builder(context, ChannelTransfer)
                .SetSmallIcon(Resource.Drawable.wisp_notification)
                .SetContentTitle("Wisp")
                .SetContentText(text)
                .SetProgress(100, Math.Clamp(percent, 0, 100), false)
                .SetOngoing(true)
                .SetOnlyAlertOnce(true)
                .SetCategory(NotificationCompat.CategoryProgress)
                .SetVisibility(NotificationCompat.VisibilityPrivate)
                .SetContentIntent(LaunchPendingIntent(context))
                .Build();
            NotificationManagerCompat.From(context)
                .Notify(TransferNotificationId, notification);
        }
        catch
        {
        }
    }

    /// <summary>Posts a one-time success/failure alert.</summary>
    public static void PostResult(Context context, bool success, string title, string message)
    {
        try
        {
            CreateResultChannel(context);
            if (!CanPostNotifications(context)) return;
            var notification = new NotificationCompat.Builder(context, ChannelResult)
                .SetSmallIcon(Resource.Drawable.wisp_notification)
                .SetContentTitle(title)
                .SetContentText(message)
                .SetStyle(new NotificationCompat.BigTextStyle().BigText(message))
                .SetAutoCancel(true)
                .SetCategory(success
                    ? NotificationCompat.CategoryStatus
                    : NotificationCompat.CategoryError)
                .SetVisibility(NotificationCompat.VisibilityPrivate)
                .SetContentIntent(LaunchPendingIntent(context))
                .Build();
            NotificationManagerCompat.From(context)
                .Notify(success ? 9001 : 9002, notification);
        }
        catch
        {
        }
    }

    private static bool CanPostNotifications(Context context)
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.Tiramisu) return true;
        return ContextCompat.CheckSelfPermission(context,
            global::Android.Manifest.Permission.PostNotifications) == Permission.Granted;
    }

    private static PendingIntent? LaunchPendingIntent(Context context)
    {
        var launch = new Intent(context, typeof(MainActivity));
        launch.SetFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop);
        return PendingIntent.GetActivity(context, 0, launch,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
    }

    private void CreateChannels()
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.O) return;
        var manager = (NotificationManager)GetSystemService(NotificationService)!;
        var transfer = new NotificationChannel(ChannelTransfer, "Active transfers",
            NotificationImportance.Low)
        {
            Description = "Shown while Wisp is moving files"
        };
        transfer.SetShowBadge(false);
        manager.CreateNotificationChannel(transfer);
        CreateResultChannel(this);
    }

    private static void CreateResultChannel(Context context)
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.O) return;
        var manager = (NotificationManager)context.GetSystemService(NotificationService)!;
        if (manager.GetNotificationChannel(ChannelResult) is not null) return;
        var results = new NotificationChannel(ChannelResult, "Transfer results",
            NotificationImportance.High)
        {
            Description = "Alerts when a transfer completes or fails"
        };
        manager.CreateNotificationChannel(results);
    }
}
