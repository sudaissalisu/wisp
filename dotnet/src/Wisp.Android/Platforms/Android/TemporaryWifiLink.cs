using Android.Net;
using Android.Net.Wifi;
using Android.OS;
using AndroidApplication = Android.App.Application;
using Context = Android.Content.Context;

namespace Wisp.Android;

/// <summary>
/// Joins the peer's LocalOnlyHotspot with WifiNetworkSpecifier, binds this
/// process to that network (so the native engine's TCP connections use it),
/// and releases the request again on disconnect. Android then reconnects the
/// previously saved Wi-Fi network automatically.
/// </summary>
internal sealed class TemporaryWifiLink : IDisposable
{
    private readonly ConnectivityManager _connectivity;
    private readonly Handler _handler;
    private LinkCallback? _callback;
    private Network? _network;
    private bool _disconnecting;
    private bool _disposed;

    public TemporaryWifiLink()
    {
        var context = AndroidApplication.Context;
        _connectivity = (ConnectivityManager)context.GetSystemService(Context.ConnectivityService)!;
        _handler = new Handler(Looper.MainLooper!);
    }

    public event Action<Network>? Connected;
    public event Action<string>? Failed;
    public event Action? Lost;

    public Network? Network => _network;
    public bool IsConnected => _network is not null;

    public bool Join(string ssid, string passphrase, int timeoutMs = 45000)
    {
        if (_disposed) return false;
        if (Build.VERSION.SdkInt < BuildVersionCodes.Q)
        {
            Failed?.Invoke("Joining a temporary link needs Android 10 or newer.");
            return false;
        }
        if (_callback is not null)
        {
            Failed?.Invoke("A temporary link is already active.");
            return false;
        }

        try
        {
#pragma warning disable CA1416 // Guarded by the API 29 check above.
            var specifier = new WifiNetworkSpecifier.Builder()
                .SetSsid(ssid)!
                .SetWpa2Passphrase(passphrase)!
                .Build();
            var request = new NetworkRequest.Builder()
                .AddTransportType(TransportType.Wifi)!
                .SetNetworkSpecifier(specifier)!
                .Build()!;
#pragma warning restore CA1416
            var callback = new LinkCallback(this);
            _callback = callback;
            _disconnecting = false;
            _connectivity.RequestNetwork(request, callback, _handler, Math.Max(timeoutMs, 10000));
            return true;
        }
        catch (Exception exception)
        {
            _callback = null;
            Failed?.Invoke($"Could not join the temporary Wi-Fi: {exception.Message}");
            return false;
        }
    }

    public void Disconnect()
    {
        _disconnecting = true;
        var callback = _callback;
        _callback = null;
        _network = null;
        try
        {
            if (callback is not null) _connectivity.UnregisterNetworkCallback(callback);
        }
        catch
        {
        }
        try
        {
            _connectivity.BindProcessToNetwork(null);
        }
        catch
        {
        }
        _disconnecting = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disconnect();
    }

    private void HandleAvailable(Network network)
    {
        if (_disposed || _disconnecting) return;
        if (!_connectivity.BindProcessToNetwork(network))
        {
            Failed?.Invoke("Android connected to the temporary Wi-Fi but would not route Wisp through it.");
            Disconnect();
            return;
        }
        _network = network;
        Connected?.Invoke(network);
    }

    private void HandleLost(Network network)
    {
        if (_disconnecting || _disposed) return;
        _network = null;
        Lost?.Invoke();
    }

    private void HandleUnavailable()
    {
        if (_disconnecting || _disposed) return;
        var callback = _callback;
        _callback = null;
        _network = null;
        try
        {
            if (callback is not null) _connectivity.UnregisterNetworkCallback(callback);
        }
        catch
        {
        }
        _connectivity.BindProcessToNetwork(null);
        Failed?.Invoke("The other phone's temporary Wi-Fi did not answer. Keep its QR screen open, make sure Location is on, and try again.");
    }

    private sealed class LinkCallback : ConnectivityManager.NetworkCallback
    {
        private readonly TemporaryWifiLink _owner;

        public LinkCallback(TemporaryWifiLink owner) => _owner = owner;

        public override void OnAvailable(Network network) => _owner.HandleAvailable(network);

        public override void OnLost(Network network) => _owner.HandleLost(network);

        public override void OnUnavailable() => _owner.HandleUnavailable();
    }
}
