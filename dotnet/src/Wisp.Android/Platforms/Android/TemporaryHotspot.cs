using Android.Net.Wifi;
using Android.OS;
using AndroidApplication = Android.App.Application;
using Context = Android.Content.Context;

namespace Wisp.Android;

internal sealed record HotspotCredentials(string Ssid, string Passphrase);

/// <summary>
/// Owns the Android LocalOnlyHotspot used for a QR-created temporary link.
/// The hotspot has no internet access and is stopped when the reservation is
/// closed, which also lets Android restore the previous Wi-Fi connection.
/// </summary>
internal sealed class TemporaryHotspot : IDisposable
{
    private readonly WifiManager _wifi;
    private readonly Handler _handler;
    private readonly HotspotCallback _callback;
    private WifiManager.LocalOnlyHotspotReservation? _reservation;
    private bool _starting;
    private bool _disposed;

    public TemporaryHotspot()
    {
        var context = AndroidApplication.Context;
        _wifi = (WifiManager)context.GetSystemService(Context.WifiService)!;
        _handler = new Handler(Looper.MainLooper!);
        _callback = new HotspotCallback(this);
    }

    public event Action<HotspotCredentials>? Started;
    public event Action<string>? Failed;
    public event Action? Stopped;

    public bool IsActive => _reservation is not null;

    public void Start()
    {
        if (_disposed || _starting || _reservation is not null) return;
        if (Build.VERSION.SdkInt < BuildVersionCodes.O)
        {
            Failed?.Invoke("Temporary links need Android 8 or newer.");
            return;
        }
        _starting = true;
        try
        {
            _wifi.StartLocalOnlyHotspot(_callback, _handler);
        }
        catch (Exception exception)
        {
            _starting = false;
            Failed?.Invoke($"Could not start the hotspot: {exception.Message}");
        }
    }

    public void Stop()
    {
        _starting = false;
        var reservation = _reservation;
        _reservation = null;
        try
        {
            reservation?.Close();
            reservation?.Dispose();
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    private void HandleStarted(WifiManager.LocalOnlyHotspotReservation? reservation)
    {
        _starting = false;
        if (_disposed)
        {
            reservation?.Close();
            reservation?.Dispose();
            return;
        }
        if (reservation is null)
        {
            Failed?.Invoke("Android did not return a hotspot reservation.");
            return;
        }
        string? ssid = null;
        string? passphrase = null;
        try
        {
#pragma warning disable CA1416, CA1422 // Guarded by the Android 11 check.
            if (Build.VERSION.SdkInt >= BuildVersionCodes.R &&
                reservation.SoftApConfiguration is { } softAp)
            {
                if (IsUsableCredential(softAp.Ssid)) ssid = softAp.Ssid;
                if (IsUsableCredential(softAp.Passphrase)) passphrase = softAp.Passphrase;
            }
            // Some Android 13+ builds redact SoftApConfiguration fields; the
            // reservation's legacy configuration still has the values for the
            // app that owns the local-only hotspot.
            if (!IsUsableCredential(ssid) || !IsUsableCredential(passphrase))
            {
                var legacy = reservation.WifiConfiguration;
                if (!IsUsableCredential(ssid)) ssid = legacy?.Ssid?.Trim('"');
                if (!IsUsableCredential(passphrase))
                    passphrase = legacy?.PreSharedKey?.Trim('"');
            }
#pragma warning restore CA1416, CA1422
        }
        catch
        {
        }
        if (!IsUsableCredential(ssid) || !IsUsableCredential(passphrase))
        {
            reservation.Close();
            reservation.Dispose();
            Failed?.Invoke("Android did not expose the temporary Wi-Fi credentials.");
            return;
        }
        _reservation = reservation;
        Started?.Invoke(new HotspotCredentials(ssid!, passphrase!));
    }

    private static bool IsUsableCredential(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !value.Contains("redacted", StringComparison.OrdinalIgnoreCase);

    private void HandleFailed(LocalOnlyHotspotCallbackErrorCode reason)
    {
        _starting = false;
        var message = reason switch
        {
            LocalOnlyHotspotCallbackErrorCode.NoChannel => "No Wi-Fi channel is available for the hotspot.",
            LocalOnlyHotspotCallbackErrorCode.IncompatibleMode => "Wi-Fi is busy. Turn off any existing hotspot and try again.",
            LocalOnlyHotspotCallbackErrorCode.TetheringDisallowed => "Hotspot use is disabled by this device or its work profile.",
            _ => "Android could not start the temporary hotspot."
        };
        Failed?.Invoke(message);
    }

    private void HandleStopped()
    {
        _reservation = null;
        _starting = false;
        Stopped?.Invoke();
    }

    private sealed class HotspotCallback : WifiManager.LocalOnlyHotspotCallback
    {
        private readonly TemporaryHotspot _owner;

        public HotspotCallback(TemporaryHotspot owner) => _owner = owner;

        public override void OnStarted(WifiManager.LocalOnlyHotspotReservation? reservation) =>
            _owner.HandleStarted(reservation);

        public override void OnFailed(LocalOnlyHotspotCallbackErrorCode reason) =>
            _owner.HandleFailed(reason);

        public override void OnStopped() => _owner.HandleStopped();
    }
}
