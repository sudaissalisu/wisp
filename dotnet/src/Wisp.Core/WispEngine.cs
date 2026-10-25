using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Wisp.Core;

/// <summary>
/// Managed wrapper around the native Wisp transfer engine. Events are raised on
/// native worker threads; UI callers must marshal them to their dispatcher.
/// </summary>
public sealed class WispEngine : IDisposable
{
    private const int EventPeerFound = 1;
    private const int EventPeerLost = 2;
    private const int EventOffer = 3;
    private const int EventProgress = 4;
    private const int EventPairing = 5;
    private const int EventComplete = 6;
    private const int EventError = 7;
    private const int EventCancelled = 8;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    // The native engine stores these function pointers beyond the create call.
    private static readonly NativeMethods.EventCallback s_eventCallback = OnNativeEvent;
    private static readonly NativeMethods.LogCallback s_logCallback = OnNativeLog;

    private readonly object _gate = new();
    private readonly Dictionary<string, PeerInfo> _peers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _lastSeenUtc = new();
    private readonly HashSet<string> _pendingRemoval = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _removalTimer;
    private IntPtr _handle;
    private GCHandle _selfHandle;
    private bool _started;
    private bool _disposed;

    public PeerInfo? Self { get; private set; }
    public string PeerId => Self?.Id ?? "";
    public string Fingerprint => Self?.Fingerprint ?? "";
    public string DisplayName => Self?.Name ?? "";
    public int ListenPort => Self?.Port ?? 0;

    public event EventHandler<PeerEventArgs>? PeerChanged;
    public event EventHandler<TransferOffer>? OfferReceived;
    public event EventHandler<PairingInfo>? PairingCodeReceived;
    public event EventHandler<TransferProgress>? ProgressChanged;
    public event EventHandler<TransferCompletion>? TransferCompleted;
    public event EventHandler<TransferFailure>? TransferFailed;
    public event EventHandler<TransferFailure>? TransferCancelled;
    public event EventHandler<LogEventArgs>? LogReceived;

    public WispEngine()
    {
        _removalTimer = new Timer(_ => FlushExpiredPeers(), null,
            PeerGrace, TimeSpan.FromSeconds(5));
    }

    public IReadOnlyList<PeerInfo> Peers
    {
        get
        {
            lock (_gate)
            {
                return _peers.Values.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
            }
        }
    }

    // Wi-Fi power save and access-point filtering can make beacons arrive in
    // bursts; removing a peer the instant one expiry fires makes the nearby list
    // flicker. Keep peers visible for a grace window after the last sighting.
    private static readonly TimeSpan PeerGrace = TimeSpan.FromSeconds(30);

    public void Start(WispConfig config)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started) return;

        var name = Marshal.StringToCoTaskMemUTF8(config.Name ?? "");
        var save = Marshal.StringToCoTaskMemUTF8(config.SaveDirectory);
        var identity = Marshal.StringToCoTaskMemUTF8(config.IdentityDirectory);
        try
        {
            var nativeConfig = new NativeMethods.EngineConfigNative
            {
                Name = name,
                SaveDirectory = save,
                IdentityDirectory = identity,
                ListenPort = config.ListenPort,
                DiscoveryPort = config.DiscoveryPort,
                AutoAccept = config.AutoAccept ? 1 : 0,
                DataChannels = config.DataChannels,
                ConflictPolicy = config.ConflictPolicy
            };

            _selfHandle = GCHandle.Alloc(this);
            _handle = NativeMethods.wisp_engine_create(
                ref nativeConfig,
                s_eventCallback,
                s_logCallback,
                GCHandle.ToIntPtr(_selfHandle));
            if (_handle == IntPtr.Zero)
            {
                _selfHandle.Free();
                throw new InvalidOperationException("The Wisp native engine could not be created.");
            }

            var result = NativeMethods.wisp_engine_start(_handle);
            if (result != 0)
            {
                NativeMethods.wisp_engine_destroy(_handle);
                _handle = IntPtr.Zero;
                _selfHandle.Free();
                throw new InvalidOperationException($"The Wisp engine failed to start (code {result}).");
            }
            _started = true;
            Self = Deserialize<PeerInfo>(GetSelfJson());
        }
        catch
        {
            Marshal.FreeCoTaskMem(name);
            Marshal.FreeCoTaskMem(save);
            Marshal.FreeCoTaskMem(identity);
            throw;
        }
        Marshal.FreeCoTaskMem(name);
        Marshal.FreeCoTaskMem(save);
        Marshal.FreeCoTaskMem(identity);
    }

    public void Stop()
    {
        if (!_started) return;
        var handle = _handle;
        if (handle != IntPtr.Zero) NativeMethods.wisp_engine_stop(handle);
        _started = false;
    }

    public string Send(IEnumerable<string> paths, string peerId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var list = paths.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
        if (list.Length == 0) throw new ArgumentException("Choose at least one file or folder.", nameof(paths));
        if (string.IsNullOrEmpty(peerId)) throw new ArgumentException("No nearby device selected.", nameof(peerId));

        var pointers = new IntPtr[list.Length];
        var transferIdBuffer = Marshal.AllocHGlobal(65);
        try
        {
            for (var i = 0; i < list.Length; i++)
                pointers[i] = Marshal.StringToCoTaskMemUTF8(list[i]);
            Marshal.WriteByte(transferIdBuffer, 0);

            var result = NativeMethods.wisp_engine_send_paths(
                _handle, peerId, pointers, (UIntPtr)pointers.Length,
                transferIdBuffer, (UIntPtr)65);
            if (result != 0)
                throw new InvalidOperationException($"The transfer could not be started (code {result}).");

            return Marshal.PtrToStringUTF8(transferIdBuffer) ?? "";
        }
        finally
        {
            foreach (var pointer in pointers)
            {
                if (pointer != IntPtr.Zero) Marshal.FreeCoTaskMem(pointer);
            }
            Marshal.FreeHGlobal(transferIdBuffer);
        }
    }

    public bool Respond(string offerId, bool accept, string? saveDirectory = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return NativeMethods.wisp_engine_respond(_handle, offerId, accept ? 1 : 0,
            string.IsNullOrWhiteSpace(saveDirectory) ? null : saveDirectory) == 0;
    }

    public bool Cancel(string transferId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return NativeMethods.wisp_engine_cancel(_handle, transferId) == 0;
    }

    public bool AddPeer(PeerInfo peer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var result = NativeMethods.wisp_engine_add_peer(
            _handle,
            string.IsNullOrWhiteSpace(peer.Id) ? null : peer.Id,
            string.IsNullOrWhiteSpace(peer.Name) ? null : peer.Name,
            peer.Host,
            peer.Port,
            string.IsNullOrWhiteSpace(peer.Fingerprint) ? null : peer.Fingerprint);
        return result == 0;
    }

    public IReadOnlyList<PeerInfo> RefreshPeers()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var json = GetPeersJson();
        var peers = Deserialize<List<PeerInfo>>(json) ?? [];
        lock (_gate)
        {
            _peers.Clear();
            foreach (var peer in peers)
            {
                if (peer.Online) _peers[peer.Id] = peer;
            }
        }
        return Peers;
    }

    public static string Version
    {
        get
        {
            var pointer = NativeMethods.wisp_version();
            return pointer == IntPtr.Zero ? "unknown" : Marshal.PtrToStringUTF8(pointer) ?? "unknown";
        }
    }

    private void FlushExpiredPeers()
    {
        if (_disposed) return;
        List<PeerInfo>? expired = null;
        lock (_gate)
        {
            var cutoff = DateTime.UtcNow - PeerGrace;
            foreach (var id in _pendingRemoval.ToList())
            {
                if (_lastSeenUtc.TryGetValue(id, out var seen) && seen <= cutoff)
                {
                    if (_peers.TryGetValue(id, out var peer))
                    {
                        expired ??= [];
                        expired.Add(peer);
                    }
                    _peers.Remove(id);
                    _lastSeenUtc.Remove(id);
                    _pendingRemoval.Remove(id);
                }
            }
        }
        if (expired is null) return;
        foreach (var peer in expired) Raise(PeerChanged, new PeerEventArgs(peer, false));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _removalTimer.Dispose();
        var handle = _handle;
        _handle = IntPtr.Zero;
        if (handle != IntPtr.Zero)
        {
            NativeMethods.wisp_engine_destroy(handle);
            _started = false;
        }
        if (_selfHandle.IsAllocated) _selfHandle.Free();
        lock (_gate) _peers.Clear();
    }

    private string GetSelfJson()
    {
        const int capacity = 16 * 1024;
        var buffer = Marshal.AllocHGlobal(capacity);
        try
        {
            Marshal.WriteByte(buffer, 0);
            NativeMethods.wisp_engine_get_self(_handle, buffer, (UIntPtr)capacity);
            return Marshal.PtrToStringUTF8(buffer) ?? "{}";
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private string GetPeersJson()
    {
        const int capacity = 1024 * 1024;
        var buffer = Marshal.AllocHGlobal(capacity);
        try
        {
            Marshal.WriteByte(buffer, 0);
            var length = NativeMethods.wisp_engine_get_peers(_handle, buffer, (UIntPtr)capacity);
            if (length < 0) return "[]";
            return Marshal.PtrToStringUTF8(buffer) ?? "[]";
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static T? Deserialize<T>(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (JsonException exception)
        {
            Debug.WriteLine($"Wisp event JSON parse failed: {exception.Message} {json}");
            return default;
        }
    }

    private static void OnNativeEvent(IntPtr user, int type, IntPtr jsonPointer)
    {
        if (user == IntPtr.Zero) return;
        try
        {
            if (GCHandle.FromIntPtr(user).Target is not WispEngine engine) return;
            engine.HandleEvent(type, Marshal.PtrToStringUTF8(jsonPointer) ?? "{}");
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Wisp event callback failed: {exception}");
        }
    }

    private static void OnNativeLog(IntPtr user, int level, IntPtr messagePointer)
    {
        if (user == IntPtr.Zero) return;
        try
        {
            if (GCHandle.FromIntPtr(user).Target is not WispEngine engine) return;
            engine.Raise(engine.LogReceived, new LogEventArgs(level, Marshal.PtrToStringUTF8(messagePointer) ?? ""));
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Wisp log callback failed: {exception}");
        }
    }

    private void HandleEvent(int type, string json)
    {
        switch (type)
        {
        case EventPeerFound:
        case EventPeerLost:
        {
            var peer = Deserialize<PeerInfo>(json);
            if (peer is null || string.IsNullOrEmpty(peer.Id)) return;
            if (peer.Online)
            {
                bool announce;
                lock (_gate)
                {
                    if (_peers.TryGetValue(peer.Id, out var known))
                    {
                        announce = known.Host != peer.Host || known.Port != peer.Port ||
                                   known.Fingerprint != peer.Fingerprint;
                    }
                    else
                    {
                        announce = true;
                    }
                    _peers[peer.Id] = peer;
                    _lastSeenUtc[peer.Id] = DateTime.UtcNow;
                    _pendingRemoval.Remove(peer.Id);
                }
                if (announce) Raise(PeerChanged, new PeerEventArgs(peer, true));
            }
            else
            {
                // Start the grace window once; a refreshed sighting cancels it.
                lock (_gate)
                {
                    if (_pendingRemoval.Add(peer.Id))
                    {
                        _lastSeenUtc[peer.Id] = DateTime.UtcNow;
                    }
                }
            }
            break;
        }
        case EventOffer:
        {
            var offer = Deserialize<TransferOffer>(json);
            if (offer is not null) Raise(OfferReceived, offer);
            break;
        }
        case EventPairing:
        {
            var pairing = Deserialize<PairingInfo>(json);
            if (pairing is not null) Raise(PairingCodeReceived, pairing);
            break;
        }
        case EventProgress:
        {
            var progress = Deserialize<TransferProgress>(json);
            if (progress is not null && !string.IsNullOrEmpty(progress.TransferId))
                Raise(ProgressChanged, progress);
            break;
        }
        case EventComplete:
        {
            var completion = Deserialize<TransferCompletion>(json);
            if (completion is not null) Raise(TransferCompleted, completion);
            break;
        }
        case EventError:
        case EventCancelled:
        {
            var failure = Deserialize<TransferFailure>(json) ?? new TransferFailure { Message = "Transfer failed." };
            if (type == EventCancelled) Raise(TransferCancelled, failure);
            else Raise(TransferFailed, failure);
            break;
        }
        }
    }

    private void Raise<T>(EventHandler<T>? handler, T args)
    {
        if (handler is null) return;
        foreach (var callback in handler.GetInvocationList().Cast<EventHandler<T>>())
        {
            try
            {
                callback(this, args);
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"Wisp event handler failed: {exception}");
            }
        }
    }
}
