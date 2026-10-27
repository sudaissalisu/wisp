using System.Collections.ObjectModel;
using System.Security.Cryptography;
using Android.Content;
using Android.Content.PM;
using Android.Net;
using Android.Net.Wifi;
using Android.OS;
using AndroidX.Core.App;
using AndroidX.Core.Content;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using Wisp.Core;
using ZXing.Net.Maui;
using ZXing.Net.Maui.Controls;
using AndroidApplication = Android.App.Application;
using SystemEnvironment = System.Environment;
using MauiColor = Microsoft.Maui.Graphics.Color;

namespace Wisp.Android;

public partial class MainPage : ContentPage
{
    private static readonly MauiColor MintColor = MauiColor.FromArgb("#45E3B4");
    private static readonly MauiColor CoralColor = MauiColor.FromArgb("#FF7168");
    private static readonly MauiColor OffColor = MauiColor.FromArgb("#66726F");
    private static readonly MauiColor FaintLightColor = MauiColor.FromArgb("#8A948F");
    private static readonly MauiColor FaintDarkColor = MauiColor.FromArgb("#66726F");

    private const string IconCheck = "\uE86C";
    private const string IconError = "\uE001";
    private const string IconInfo = "\uE88F";

    private static readonly TimeSpan TemporaryLinkLifetime = TimeSpan.FromMinutes(10);

    private readonly Dictionary<string, PeerItem> _peerById = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TransferItem> _transferById = new(StringComparer.OrdinalIgnoreCase);
    private readonly SettingsStore _settingsStore;
    private readonly TrustStore _trustStore;
    private readonly HistoryStore _historyStore;
    private readonly string _identityDirectory;
    private readonly ActivityGaugeDrawable _gaugeDrawable = new();
    private readonly SemaphoreSlim _engineGate = new(1, 1);
    private AppSettings _settings;
    private WispEngine? _engine;
    private WifiManager.MulticastLock? _multicastLock;
    private Network? _boundWifiNetwork;
    private IDispatcherTimer? _toastTimer;
    private IDispatcherTimer? _gaugeTimer;
    private IDispatcherTimer? _linkExpiryTimer;
    private CancellationTokenSource? _pulseCts;
    private TemporaryHotspot? _temporaryHotspot;
    private TemporaryWifiLink? _temporaryWifiLink;
    private CameraBarcodeReaderView? _linkScanner;
    private QrLinkPayload? _activeLink;
    private PeerInfo? _linkPeer;
    private string _temporaryLinkRole = "";
    private bool _temporaryLinkActive;
    private bool _temporaryLinkBusy;
    private bool _temporaryLinkClosing;
    private bool _linkPeerJoined;
    private string[]? _linkSendAfterConnect;
    private bool _scannerHandling;
    private bool _scanPending;
    private TransferOffer? _pendingOffer;
    private bool _started;
    private bool _closing;
    private bool _foregroundActive;
    private bool _processingShares;
    private string _activeSection = "nearby";
    private readonly List<string> _sectionHistory = [];
    private bool _navigatingBack;
    private string _settingsCategory = "general";
    private readonly List<string> _pendingSharePaths = [];
    private List<LibraryFile> _librarySnapshot = [];
    private string _libraryFilter = "all";
    private bool _libraryLoading;
    private CancellationTokenSource? _miniDockHide;
    private int _miniDockToken;
    private string? _activeTransferId;
    private bool _transferMinimized;
    private bool _unlocked;
    private bool _unlockPromptActive;
    private bool _appVisible;
    private int _lastPeerCount;
    private long _lastProgressNotificationMs;
    private long _lastProgressLogMs;
    private int _progressEventCount;
    private long _lastBytesTotal;
    private long _lastBytesDone;
    private int _lastFilesTotal;

    public MainPage()
    {
        InitializeComponent();
        BindingContext = this;

        var filesDir = FileSystem.AppDataDirectory;
        _identityDirectory = Path.Combine(filesDir, "identity");
        _settingsStore = new SettingsStore(Path.Combine(filesDir, "settings.json"));
        _trustStore = new TrustStore(Path.Combine(filesDir, "trusted.json"));
        _historyStore = new HistoryStore(Path.Combine(filesDir, "history.json"));
        _settings = _settingsStore.Load();
        if (string.IsNullOrWhiteSpace(_settings.DeviceName) ||
            _settings.DeviceName.Contains("'s device", StringComparison.Ordinal))
            _settings.DeviceName = $"{Build.Model} Android";
        _settings.SaveDirectory = AndroidFileService.ReceivedRoot;
        AndroidFileService.ResetStaging();
        SoundService.SetEnabled(_settings.SoundsEnabled);
        SoundService.Initialize();

        DeviceNameEntry.Text = _settings.DeviceName;
        SaveFolderLabel.Text = _settings.SaveDirectory;
        AutoAcceptSwitch.IsToggled = _settings.AutoAcceptTrusted;
        SoundsSwitch.IsToggled = _settings.SoundsEnabled;
        NotificationsSwitch.IsToggled = _settings.NotificationsEnabled;
        BiometricAcceptSwitch.IsToggled = _settings.BiometricAccept;
        BiometricUnlockSwitch.IsToggled = _settings.BiometricUnlock;
        UpdateThemePills();
        ShowSettingsMenu();
        AboutLabel.Text = "Wisp · A project by SSM Limited";
        AboutVersionLabel.Text =
            $"Version {AppInfo.Current.VersionString} · Android {Build.VERSION.Release}";
        OnboardingOverlay.IsVisible = !_settings.Onboarded;

        HeaderGauge.Drawable = _gaugeDrawable;
        RefreshTrusted();
        RefreshHistory();
        SetSectionInitial();
        UpdatePeerUi();

        ShareInbox.Changed += OnShareInboxChanged;
        AppActionInbox.Changed += OnAppActionChanged;
    }

    public ObservableCollection<PeerItem> Peers { get; } = [];
    public ObservableCollection<TransferItem> Transfers { get; } = [];
    public ObservableCollection<HistoryItem> History { get; } = [];
    public ObservableCollection<LibraryFile> Library { get; } = [];
    public ObservableCollection<TrustedItem> Trusted { get; } = [];

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _appVisible = false;
        MediaViewer.DismissCurrent();
        // Release the camera while the app is not visible; the temporary link
        // itself (hotspot or joined network) stays up for in-flight transfers.
        if (LinkOverlay.IsVisible && LinkScanPanel.IsVisible)
        {
            _scanPending = true;
            ReleaseScanner();
        }
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        _appVisible = true;
        StartPresencePulse();
        AndroidTheme.Apply(Application.Current?.RequestedTheme == AppTheme.Light);
        if (_scanPending && LinkOverlay.IsVisible && LinkScanPanel.IsVisible && !_temporaryLinkActive)
        {
            _scanPending = false;
            StartScannerDetection();
        }
        if (!_settings.Onboarded)
        {
            OnboardingOverlay.IsVisible = true;
            return;
        }
        if (_settings.BiometricUnlock && !_unlocked)
        {
            LockOverlay.IsVisible = true;
            await TryUnlockAsync();
        }
        if (!_started)
        {
            _started = true;
            await StartEngineAsync();
        }
        _ = ProcessSharedFilesAsync();
        await ProcessAppActionsAsync();
        if (_activeSection == "files") _ = RefreshLibraryAsync(force: true);
    }

    /// <summary>
    /// Handles Android back in this single-page app: close the topmost sheet,
    /// leave a settings category, or return to the previous tab. Returns false
    /// only when there is nothing left to close, which lets Android leave the app.
    /// </summary>
    public bool HandleBackNavigation()
    {
        if (_closing) return false;
        if (LinkOverlay.IsVisible)
        {
            CloseTemporaryLink_Click(this, EventArgs.Empty);
            return true;
        }
        if (ShareSheetOverlay.IsVisible)
        {
            CloseShareSheet_Click(this, EventArgs.Empty);
            return true;
        }
        if (OfferOverlay.IsVisible)
        {
            // Back declines explicitly so the sender is not left waiting.
            DeclineOffer_Click(this, EventArgs.Empty);
            return true;
        }
        if (TransferOverlay.IsVisible)
        {
            // Keep the transfer running in the background; back only minimizes.
            MinimizeTransfer_Click(this, EventArgs.Empty);
            return true;
        }
        if (LockOverlay.IsVisible || OnboardingOverlay.IsVisible) return true;
        if (SettingsDetailHeader.IsVisible)
        {
            ShowSettingsMenu();
            return true;
        }
        if (_activeSection != "nearby")
        {
            if (!GoBackToPreviousSection()) SetSection("nearby");
            return true;
        }
        return false;
    }

    private bool GoBackToPreviousSection()
    {
        while (_sectionHistory.Count > 0)
        {
            var index = _sectionHistory.Count - 1;
            var target = _sectionHistory[index];
            _sectionHistory.RemoveAt(index);
            if (target == _activeSection) continue;
            _navigatingBack = true;
            try
            {
                SetSection(target);
            }
            finally
            {
                _navigatingBack = false;
            }
            return true;
        }
        return false;
    }

    protected override bool OnBackButtonPressed() => HandleBackNavigation();

    public void Shutdown()
    {
        if (_closing) return;
        _closing = true;
        MediaViewer.DismissCurrent();
        CancelMiniDockHide();
        ShareInbox.Changed -= OnShareInboxChanged;
        AppActionInbox.Changed -= OnAppActionChanged;
        StopPresencePulse();
        StopGaugeTimer();
        StopTemporaryLinkResources();
        var engine = _engine;
        _engine = null;
        if (engine is not null)
        {
            Unsubscribe(engine);
            _ = Task.Run(engine.Dispose);
        }
        ReleaseMulticastLock();
        UnbindWifiNetwork();
        StopForegroundService();
        try { WispWidgetProvider.Update(AndroidApplication.Context, "offline", "Wisp", "Offline", -1); } catch { }
    }

    // ── Engine lifecycle ──────────────────────────────────────────────

    private async Task<bool> StartEngineAsync(bool bindWifi = true, bool discovery = true,
        bool announce = true, bool clearBindings = true)
    {
        await _engineGate.WaitAsync();
        try
        {
            if (_closing) return false;
            var old = _engine;
            _engine = null;
            if (old is not null)
            {
                Unsubscribe(old);
                await Task.Run(old.Dispose);
            }
            lock (_peerById)
            {
                _peerById.Clear();
                Peers.Clear();
            }
            _lastPeerCount = 0;
            UpdatePeerUi();
            SetHeaderStatus("starting…", false);
            PresenceStatusLabel.Text = "Connecting…";
            UpdateWidget("idle", "Wisp", "Starting…", -1);

            AndroidPermissions.RequestAll();
            if (bindWifi) BindToWifiNetwork();
            else if (clearBindings) UnbindWifiNetwork();
            AcquireMulticastLock();

            var engine = new WispEngine();
            _engine = engine;
            Subscribe(engine);
            try
            {
                await Task.Run(() => engine.Start(new WispConfig
                {
                    Name = _settings.DeviceName,
                    SaveDirectory = _settings.SaveDirectory,
                    IdentityDirectory = _identityDirectory,
                    DiscoveryPort = discovery ? 47778 : 0,
                    DataChannels = 0
                }));
                UpdateIdentityUi();
                SetHeaderStatus("visible", true);
                PresenceStatusLabel.Text = _temporaryLinkActive
                    ? "Private link active"
                    : "Visible on this Wi-Fi";
                PresenceDot.Color = MintColor;
                StartForegroundService(_temporaryLinkActive
                    ? "Temporary link active · transfer safely"
                    : "Visible nearby · transfers keep running");
                if (announce) ShowToast("You’re live", IconCheck);
                UpdateWidget("idle", _settings.DeviceName,
                    _temporaryLinkActive ? "Temporary link" : "Ready nearby", -1);
                await ProcessAppActionsAsync();
                return true;
            }
            catch (Exception exception)
            {
                _engine = null;
                Unsubscribe(engine);
                engine.Dispose();
                SetHeaderStatus("offline", false);
                PresenceStatusLabel.Text = "Engine offline";
                ShowToast($"Could not start: {exception.Message}", IconError, error: true, durationSeconds: 6);
                UpdateWidget("offline", "Wisp", "Offline", -1);
                return false;
            }
        }
        finally
        {
            _engineGate.Release();
        }
    }

    private void Subscribe(WispEngine engine)
    {
        engine.PeerChanged += OnPeerChanged;
        engine.OfferReceived += OnOfferReceived;
        engine.PairingCodeReceived += OnPairing;
        engine.ProgressChanged += OnProgress;
        engine.TransferCompleted += OnCompleted;
        engine.TransferFailed += OnFailed;
        engine.TransferCancelled += OnCancelled;
        engine.LogReceived += OnLog;
    }

    private void Unsubscribe(WispEngine engine)
    {
        engine.PeerChanged -= OnPeerChanged;
        engine.OfferReceived -= OnOfferReceived;
        engine.PairingCodeReceived -= OnPairing;
        engine.ProgressChanged -= OnProgress;
        engine.TransferCompleted -= OnCompleted;
        engine.TransferFailed -= OnFailed;
        engine.TransferCancelled -= OnCancelled;
        engine.LogReceived -= OnLog;
    }

    // ── Engine events ─────────────────────────────────────────────────

    private void OnPeerChanged(object? sender, PeerEventArgs e)
    {
        if (!ReferenceEquals(sender, _engine)) return;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var wasEmpty = Peers.Count == 0;
            ApplyPeer(e);
            if (_appVisible && _settings.SoundsEnabled && e.Online && wasEmpty && Peers.Count > 0)
                SoundService.Play("connect");
            else if (_appVisible && _settings.SoundsEnabled && !e.Online && Peers.Count == 0)
                SoundService.Play("disconnect");
        });
    }

    private void OnOfferReceived(object? sender, TransferOffer offer)
    {
        if (!ReferenceEquals(sender, _engine)) return;
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            if (_temporaryLinkActive && _temporaryLinkRole == "host")
            {
                _linkPeerJoined = true;
                _linkPeer = _engine?.Peers.FirstOrDefault(peer =>
                    peer.Id.Equals(offer.PeerId, StringComparison.OrdinalIgnoreCase)) ?? _linkPeer;
                StopLinkExpiryTimer();
                LinkSubtitleLabel.Text = $"{offer.PeerName} joined your temporary link.";
                LinkSummaryLabel.Text = $"Connected · receiving from {offer.PeerName}";
                LinkExpiryLabel.Text = "Link is in use until you disconnect.";
                LinkHostHintLabel.Text = $"{offer.PeerName} is connected. You can send files either way.";
                UpdateLinkCard();
            }
            await HandleOfferAsync(offer);
        });
    }

    private void OnPairing(object? sender, PairingInfo pairing)
    {
        if (!ReferenceEquals(sender, _engine)) return;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            // A trusted peer's certificate is already pinned, so the paired
            // key already proves the same device. Showing the short code again
            // would just ask the user to redo a check they finished earlier.
            if (IsTrustedPeer(pairing.PeerId)) return;
            ShowToast($"Verify code {pairing.Code} with {pairing.PeerName}", IconInfo, durationSeconds: 12);
        });
    }

    private bool IsTrustedPeer(string peerId)
    {
        if (string.IsNullOrWhiteSpace(peerId)) return false;
        var fingerprint = _engine?.Peers
            .FirstOrDefault(peer => peer.Id.Equals(peerId, StringComparison.OrdinalIgnoreCase))
            ?.Fingerprint;
        if (string.IsNullOrWhiteSpace(fingerprint) && _linkPeer is not null &&
            _linkPeer.Id.Equals(peerId, StringComparison.OrdinalIgnoreCase))
        {
            fingerprint = _linkPeer.Fingerprint;
        }
        return !string.IsNullOrWhiteSpace(fingerprint) &&
               _trustStore.Find(fingerprint) is not null;
    }

    private void OnProgress(object? sender, TransferProgress progress)
    {
        if (!ReferenceEquals(sender, _engine)) return;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var item = GetOrCreateTransfer(progress.TransferId, progress.PeerName, progress.Direction);
            item.ApplyProgress(progress);
            _lastBytesTotal = progress.BytesTotal;
            _lastBytesDone = progress.BytesDone;
            _lastFilesTotal = progress.FilesTotal;
            // Keep the live transfer sheet in step with intermediate progress.
            // Without this call the sheet only changed when Complete() ran, so
            // it appeared stuck at 0% until the transfer finished.
            UpdateTransferOverlay(progress);
            UpdateTransferUi();
            InstrumentProgress(progress);
            if (_temporaryLinkActive && _linkExpiryTimer is not null)
            {
                StopLinkExpiryTimer();
                LinkExpiryLabel.Text = "Link is in use until you disconnect.";
            }
            StartForegroundService(item.IsIncoming
                ? $"Receiving from {item.PeerName}"
                : $"Sending to {item.PeerName}");
            var now = SystemEnvironment.TickCount64;
            if (_settings.NotificationsEnabled && now - _lastProgressNotificationMs > 900)
            {
                _lastProgressNotificationMs = now;
                try
                {
                    WispForegroundService.UpdateProgress(AndroidApplication.Context,
                        (int)Math.Round(progress.Fraction * 100),
                        $"{progress.FilesDone}/{progress.FilesTotal} files · {progress.SpeedText}");
                }
                catch { }
            }
            UpdateWidget("active", "Transferring",
                $"{progress.FilesDone}/{progress.FilesTotal} files · {progress.SpeedText}",
                (int)Math.Round(progress.Fraction * 100));
        });
    }

    private void InstrumentProgress(TransferProgress progress)
    {
        _progressEventCount++;
        var now = SystemEnvironment.TickCount64;
        if (_lastProgressLogMs != 0 && now - _lastProgressLogMs < 1500) return;
        _lastProgressLogMs = now;
        var itemProgress = _transferById.TryGetValue(progress.TransferId, out var transfer)
            ? transfer.Progress
            : 0;
        System.Diagnostics.Debug.WriteLine(
            $"[wisp:ui] progress #{_progressEventCount} {progress.TransferId} " +
            $"{progress.Direction} {progress.BytesDone}/{progress.BytesTotal} " +
            $"({progress.Fraction * 100:0.0}%) item={itemProgress:0.000} " +
            $"sheet={TransferProgressBar.Progress:0.000}");
    }

    private void OnCompleted(object? sender, TransferCompletion completion)
    {
        if (!ReferenceEquals(sender, _engine)) return;
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            var item = GetOrCreateTransfer(completion.TransferId, completion.PeerName, completion.Direction);
            item.Complete(completion);
            UpdateTransferUi();
            FinishTransferOverlay(completion.TransferId, success: true);
            RecordHistory(new HistoryEntry
            {
                Id = completion.TransferId,
                Direction = completion.Direction,
                PeerName = completion.PeerName,
                PeerId = completion.PeerId,
                Bytes = completion.BytesTotal,
                Files = completion.FilesTotal,
                State = "completed",
                SavedPath = completion.SavedPath
            });
            if (_appVisible && _settings.SoundsEnabled) SoundService.Play("success");
            var message = completion.IsIncoming
                ? $"Received {TransferOffer.FormatBytes(completion.BytesTotal)} from {completion.PeerName}"
                : $"Delivered {TransferOffer.FormatBytes(completion.BytesTotal)} to {completion.PeerName}";
            ShowToast(message, IconCheck);
            if (_settings.NotificationsEnabled)
            {
                try
                {
                    WispForegroundService.PostResult(AndroidApplication.Context, true,
                        completion.IsIncoming ? "Files received" : "Transfer complete", message);
                }
                catch { }
            }
            UpdateWidget("success", completion.IsIncoming ? "Files received" : "Delivered",
                message, -1);
            ResetWidgetLater();
            if (completion.IsIncoming && !string.IsNullOrEmpty(completion.SavedPath))
            {
                var saved = await AndroidFileService.ExportToDownloadsAsync(completion.SavedPath);
                if (saved is not null) ShowToast($"A copy is saved in {saved}", IconCheck);
                _ = RefreshLibraryAsync(force: true);
            }
            UpdateForegroundService();
        });
    }

    private void OnFailed(object? sender, TransferFailure failure)
    {
        if (!ReferenceEquals(sender, _engine)) return;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var item = GetOrCreateTransfer(failure.TransferId, failure.PeerName, failure.Direction);
            item.Fail(failure.Message);
            UpdateTransferUi();
            FinishTransferOverlay(failure.TransferId, success: false);
            RecordHistory(new HistoryEntry
            {
                Id = failure.TransferId,
                Direction = failure.Direction,
                PeerName = failure.PeerName,
                PeerId = failure.PeerId,
                State = "failed",
                Message = failure.Message
            });
            if (_appVisible && _settings.SoundsEnabled) SoundService.Play("failure");
            ShowToast(failure.Message, IconError, error: true);
            if (_settings.NotificationsEnabled)
            {
                try
                {
                    WispForegroundService.PostResult(AndroidApplication.Context, false,
                        "Transfer failed", failure.Message);
                }
                catch { }
            }
            UpdateWidget("failure", "Transfer failed", failure.Message, -1);
            ResetWidgetLater();
            UpdateForegroundService();
        });
    }

    private void OnCancelled(object? sender, TransferFailure failure)
    {
        if (!ReferenceEquals(sender, _engine)) return;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var item = GetOrCreateTransfer(failure.TransferId, failure.PeerName, failure.Direction);
            item.Fail("Cancelled");
            UpdateTransferUi();
            FinishTransferOverlay(failure.TransferId, success: false);
            RecordHistory(new HistoryEntry
            {
                Id = failure.TransferId,
                Direction = failure.Direction,
                PeerName = failure.PeerName,
                PeerId = failure.PeerId,
                State = "failed",
                Message = "Cancelled"
            });
            UpdateWidget("idle", "Wisp", "Ready nearby", -1);
            UpdateForegroundService();
        });
    }

    private void OnLog(object? sender, LogEventArgs e)
    {
        System.Diagnostics.Debug.WriteLine($"[wisp:{e.Level}] {e.Message}");
    }

    // ── Nearby list ───────────────────────────────────────────────────

    private void ApplyPeer(PeerEventArgs args)
    {
        if (args.Online)
        {
            if (_peerById.TryGetValue(args.Peer.Id, out var existing)) existing.Update(args.Peer);
            else
            {
                var item = new PeerItem(args.Peer);
                _peerById[args.Peer.Id] = item;
                Peers.Add(item);
            }
        }
        else if (_peerById.Remove(args.Peer.Id, out var removed))
        {
            Peers.Remove(removed);
        }
        UpdatePeerUi();
    }

    private void UpdatePeerUi()
    {
        var count = Peers.Count;
        PeerCountLabel.Text = count switch
        {
            0 => "Looking around…",
            1 => "1 device nearby",
            _ => $"{count} devices nearby"
        };
        NoPeersCard.IsVisible = count == 0;
        PeersHost.IsVisible = count > 0;
        if (_engine?.Self is { } self)
        {
            PresenceNameLabel.Text = self.Name;
            PresenceInitials.Text = AndroidPalette.InitialsOf(self.Name);
            PresenceStatusLabel.Text = _temporaryLinkActive
                ? "Private link active"
                : count == 0 ? "Visible on this Wi-Fi" : $"Visible · {count} nearby";
            PresenceSubLabel.Text = _temporaryLinkActive
                ? $"Temporary link · port {self.Port}"
                : $"Visible on this Wi-Fi · port {self.Port}";
            PeerIdLabel.Text = self.Id;
            FingerprintLabel.Text = self.Fingerprint;
        }
        UpdateHeaderGauge();
        UpdateShareSheetState();
        // While hosting, the guest appears through LAN discovery on the
        // hotspot. Only trust a peer on the hotspot subnet, and only when no
        // offer has already identified the guest.
        if (_temporaryLinkActive && _temporaryLinkRole == "host" && !_linkPeerJoined &&
            _activeLink is { } activeLink && activeLink.Host.LastIndexOf('.') > 0)
        {
            var subnet = activeLink.Host[..(activeLink.Host.LastIndexOf('.') + 1)];
            var candidate = Peers.Select(peer => peer.Info).FirstOrDefault(peer =>
                peer.Online && peer.Host.StartsWith(subnet, StringComparison.Ordinal));
            if (candidate is not null)
            {
                _linkPeer = candidate;
                _linkPeerJoined = true;
                StopLinkExpiryTimer();
                LinkExpiryLabel.Text = "Link is in use until you disconnect.";
                LinkHostHintLabel.Text =
                    $"{candidate.Name} is nearby on the private link. You can send files either way.";
                UpdateLinkCard();
            }
        }
        if (!HasActiveTransfers())
        {
            UpdateWidget("idle", _engine?.Self?.Name ?? _settings.DeviceName,
                count == 0 ? "Looking for devices nearby"
                    : count == 1 ? "1 device nearby" : $"{count} devices nearby", -1);
        }
    }

    private void UpdateIdentityUi()
    {
        var self = _engine?.Self;
        if (self is null) return;
        PresenceNameLabel.Text = self.Name;
        PresenceInitials.Text = AndroidPalette.InitialsOf(self.Name);
        PresenceSubLabel.Text = _temporaryLinkActive
            ? $"Temporary link · port {self.Port}"
            : $"Visible on this Wi-Fi · port {self.Port}";
        PeerIdLabel.Text = self.Id;
        FingerprintLabel.Text = self.Fingerprint;
        SaveFolderLabel.Text = _settings.SaveDirectory;
    }

    private void SetHeaderStatus(string text, bool online)
    {
        if (!HasActiveTransfers())
        {
            HeaderActivityLabel.Text = text;
        }
        PresenceDot.Color = online ? MintColor : OffColor;
        UpdateHeaderGauge();
    }

    // ── Header activity gauge ─────────────────────────────────────────

    private bool HasActiveTransfers() => Transfers.Any(t => t.IsActive);

    private void UpdateHeaderGauge()
    {
        var active = HasActiveTransfers();
        if (active)
        {
            var totalBytes = _lastBytesTotal;
            var doneBytes = _lastBytesDone;
            var files = _lastFilesTotal;
            var pct = totalBytes <= 0 ? 0 : doneBytes / (double)totalBytes;
            _gaugeDrawable.SetState(true, true, (float)pct, files, 0);
            HeaderActivityLabel.Text = files == 1
                ? $"1 file · {pct * 100:0}%"
                : $"{files} files · {pct * 100:0}%";
            StartGaugeTimer();
        }
        else
        {
            StopGaugeTimer();
            var online = _engine?.Self is not null;
            _gaugeDrawable.SetState(false, online, 0, 0, 0);
            HeaderActivityLabel.Text = online
                ? (Peers.Count == 0 ? "visible" : $"{Peers.Count} nearby")
                : "offline";
        }
        HeaderGauge.Invalidate();
    }

    private void StartGaugeTimer()
    {
        if (_gaugeTimer is not null) return;
        _gaugeTimer = Dispatcher.CreateTimer();
        _gaugeTimer.Interval = TimeSpan.FromMilliseconds(260);
        _gaugeTimer.IsRepeating = true;
        _gaugeTimer.Tick += (_, _) =>
        {
            var pulse = (float)(0.5 + 0.5 * Math.Sin(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 450.0));
            var totalBytes = _lastBytesTotal;
            var doneBytes = _lastBytesDone;
            var files = _lastFilesTotal;
            var pct = totalBytes <= 0 ? 0 : doneBytes / (double)totalBytes;
            _gaugeDrawable.SetState(true, true, (float)pct, files, pulse);
            HeaderActivityLabel.Text = files == 1
                ? $"1 file · {pct * 100:0}%"
                : $"{files} files · {pct * 100:0}%";
            HeaderGauge.Invalidate();
        };
        _gaugeTimer.Start();
    }

    private void StopGaugeTimer()
    {
        _gaugeTimer?.Stop();
        _gaugeTimer = null;
    }

    private void StartPresencePulse()
    {
        if (_pulseCts is not null) return;
        _pulseCts = new CancellationTokenSource();
        var token = _pulseCts.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                for (var i = 0; i <= 20 && !token.IsCancellationRequested; i++)
                {
                    var t = i / 20.0;
                    try
                    {
                        MainThread.BeginInvokeOnMainThread(() =>
                        {
                            PresencePulseRing.Scale = 1.0 + 0.18 * t;
                            PresencePulseRing.Opacity = 0.55 * (1 - t);
                        });
                        await Task.Delay(90, token);
                    }
                    catch (System.OperationCanceledException)
                    {
                        return;
                    }
                }
            }
        }, token);
    }

    private void StopPresencePulse()
    {
        _pulseCts?.Cancel();
        _pulseCts = null;
    }

    // ── Transfers ─────────────────────────────────────────────────────

    private TransferItem GetOrCreateTransfer(string id, string peerName, string direction)
    {
        if (_transferById.TryGetValue(id, out var existing)) return existing;
        var item = new TransferItem(id, string.IsNullOrWhiteSpace(peerName) ? "Nearby device" : peerName, direction);
        _transferById[id] = item;
        Transfers.Insert(0, item);
        return item;
    }

    private void UpdateTransferUi()
    {
        var count = Transfers.Count;
        TransfersEmpty.IsVisible = count == 0;
        TransfersHost.IsVisible = count > 0;
        ActiveSectionLabel.IsVisible = count > 0;
        UpdateHeaderGauge();
    }

    private void RecordHistory(HistoryEntry entry)
    {
        _historyStore.Add(entry);
        MainThread.BeginInvokeOnMainThread(() =>
        {
            History.Insert(0, new HistoryItem(entry));
            if (History.Count > 40) History.RemoveAt(History.Count - 1);
            RefreshHistoryVisibility();
        });
    }

    private void RefreshHistory()
    {
        History.Clear();
        foreach (var entry in _historyStore.Items.Take(40)) History.Add(new HistoryItem(entry));
        RefreshHistoryVisibility();
    }

    private void RefreshHistoryVisibility()
    {
        HistoryEmpty.IsVisible = History.Count == 0;
        HistoryHost.IsVisible = History.Count > 0;
        HistorySectionLabel.IsVisible = History.Count > 0;
    }

    // ── Files library ─────────────────────────────────────────────────

    private async Task RefreshLibraryAsync(bool force = false)
    {
        if (_libraryLoading && !force) return;
        _libraryLoading = true;
        try
        {
            _librarySnapshot = await LibraryFile.ScanAsync(_settings.SaveDirectory);
            ApplyLibraryFilter();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Library scan failed: {exception}");
            ShowToast("Could not read the received files.", IconError, error: true);
        }
        finally
        {
            _libraryLoading = false;
        }
    }

    private void ApplyLibraryFilter()
    {
        Library.Clear();
        var filtered = _librarySnapshot
            .Where(LibraryMatches)
            .ToList();
        foreach (var file in filtered) Library.Add(file);
        var total = filtered.Sum(file => file.Size);
        LibrarySummaryLabel.Text = filtered.Count == 0
            ? "No files yet"
            : $"{filtered.Count} file{(filtered.Count == 1 ? "" : "s")} · {TransferOffer.FormatBytes(total)}";
        LibraryHost.IsVisible = filtered.Count > 0;
        LibraryEmpty.IsVisible = filtered.Count == 0;
        LibraryEmptyTitleLabel.Text = _librarySnapshot.Count == 0
            ? "No files here yet"
            : "Nothing matches this filter";
        LibraryEmptyBodyLabel.Text = _librarySnapshot.Count == 0
            ? "Received photos, videos, music, and documents appear in this tab."
            : "Try another filter, or refresh after the next transfer finishes.";
        RefreshLibraryChips();
    }

    private bool LibraryMatches(LibraryFile file) => _libraryFilter switch
    {
        "image" => file.Kind == LibraryKind.Image,
        "video" => file.Kind == LibraryKind.Video,
        "audio" => file.Kind == LibraryKind.Audio,
        "document" => file.Kind == LibraryKind.Document,
        _ => true
    };

    private void RefreshLibraryChips()
    {
        StyleLibraryChip(LibraryAllChip, LibraryAllText, _libraryFilter == "all");
        StyleLibraryChip(LibraryPhotoChip, LibraryPhotoText, _libraryFilter == "image");
        StyleLibraryChip(LibraryVideoChip, LibraryVideoText, _libraryFilter == "video");
        StyleLibraryChip(LibraryAudioChip, LibraryAudioText, _libraryFilter == "audio");
        StyleLibraryChip(LibraryDocChip, LibraryDocText, _libraryFilter == "document");
    }

    private static void StyleLibraryChip(Border chip, Label text, bool selected)
    {
        var lightTheme = Application.Current?.RequestedTheme == AppTheme.Light;
        chip.BackgroundColor = selected ? MauiColor.FromArgb("#1F45E3B4") : Colors.Transparent;
        chip.Stroke = new SolidColorBrush(selected
            ? MintColor
            : (lightTheme ? MauiColor.FromArgb("#E2E8E5") : MauiColor.FromArgb("#1FFFFFFF")));
        text.TextColor = selected ? MintColor
            : (lightTheme ? MauiColor.FromArgb("#5B6663") : MauiColor.FromArgb("#9AA6A3"));
    }

    private async void RefreshLibrary_Click(object sender, EventArgs e)
    {
        ShowToast("Scanning received files…", IconInfo, durationSeconds: 2);
        await RefreshLibraryAsync(force: true);
    }

    private void LibraryFilter_Tapped(object? sender, TappedEventArgs e)
    {
        if (e.Parameter is not string filter || _libraryFilter == filter) return;
        _libraryFilter = filter;
        ApplyLibraryFilter();
    }

    private async void LibraryFile_Tapped(object? sender, TappedEventArgs e)
    {
        if (e.Parameter is not string path) return;
        await OpenFileAsync(path);
    }

    private async void History_Tapped(object? sender, TappedEventArgs e)
    {
        if (e.Parameter is not string id) return;
        var entry = _historyStore.Items.FirstOrDefault(item =>
            item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return;
        if (!entry.IsIncoming || !entry.Succeeded)
        {
            ShowToast(entry.IsIncoming
                ? "That transfer did not finish."
                : "Sent files stay on the other device.", IconInfo);
            return;
        }
        if (string.IsNullOrWhiteSpace(entry.SavedPath))
        {
            ShowToast("The saved folder is not available.", IconInfo);
            return;
        }
        if (File.Exists(entry.SavedPath))
        {
            await OpenFileAsync(entry.SavedPath);
            return;
        }
        if (!Directory.Exists(entry.SavedPath))
        {
            ShowToast("That folder is no longer available.", IconInfo);
            return;
        }

        List<string> files;
        try
        {
            files = Directory.EnumerateFiles(entry.SavedPath, "*", SearchOption.AllDirectories)
                .Where(path => !path.Contains(".wisp-", StringComparison.OrdinalIgnoreCase))
                .Where(path => !path.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"History folder read failed: {exception}");
            ShowToast("That folder could not be read.", IconError, error: true);
            return;
        }
        if (files.Count == 0)
        {
            ShowToast("No files are left in that transfer.", IconInfo);
            return;
        }
        if (files.Count == 1)
        {
            await OpenFileAsync(files[0]);
            return;
        }

        var labels = files.Take(25)
            .Select(path => Path.GetRelativePath(entry.SavedPath, path))
            .ToArray();
        var choice = await DisplayActionSheetAsync("Open file", "Cancel", null, labels);
        if (string.IsNullOrEmpty(choice) || choice == "Cancel") return;
        var index = Array.IndexOf(labels, choice);
        if (index >= 0) await OpenFileAsync(files[index]);
    }

    private async Task OpenFileAsync(string path)
    {
        if (!File.Exists(path))
        {
            ShowToast("That file is no longer available.", IconError, error: true);
            await RefreshLibraryAsync(force: true);
            return;
        }
        if (MediaViewer.CanPreview(path))
        {
            if (!MediaViewer.Open(path))
                ShowToast("This media file could not be opened.", IconError, error: true);
            return;
        }
        if (!AndroidFileService.OpenWithSystem(path))
            ShowToast("No installed app can open this file.", IconInfo);
    }

    // ── Offers ────────────────────────────────────────────────────────

    private async Task HandleOfferAsync(TransferOffer offer)
    {
        if (_engine is null || _closing) return;
        // The fingerprint in the offer comes from the TLS certificate of this
        // connection. If it matches a pinned device, the authenticated key is
        // the same one the user verified before and the short code is redundant.
        var trusted = _trustStore.Find(offer.Fingerprint);
        if (_settings.AutoAcceptTrusted && trusted is not null)
        {
            AcceptOffer(offer, trust: false);
            ShowToast($"Receiving from {offer.PeerName}", IconCheck);
            return;
        }

        if (_pendingOffer is not null)
        {
            _engine.Respond(offer.OfferId, false);
            return;
        }

        _pendingOffer = offer;
        OfferPeerLabel.Text = offer.PeerName;
        OfferInitials.Text = AndroidPalette.InitialsOf(offer.PeerName);
        OfferAvatar.Background = AndroidPalette.BrushFor(offer.PeerId);
        OfferSummaryLabel.Text = trusted is not null
            ? $"Trusted device · {offer.FileCountText} · {offer.SizeText}"
            : $"{offer.FileCountText} · {offer.SizeText}";
        var names = string.Join("\n", offer.Files.Take(4).Select(f => f.Path));
        if (offer.FileCount > 4) names += $"\nand {offer.FileCount - 4} more…";
        OfferFilesLabel.Text = names;
        OfferCodeLabel.Text = offer.PairingCode;
        OfferCodeCard.IsVisible = trusted is null;
        OfferTrustRow.IsVisible = trusted is null;
        OfferTrustedNote.IsVisible = trusted is not null;
        OfferTrustSwitch.IsToggled = false;
        OfferOverlay.IsVisible = true;
        OfferOverlay.Opacity = 0;
        await OfferOverlay.FadeTo(1, 180, Easing.CubicOut);
    }

    private async void AcceptOffer_Click(object sender, EventArgs e)
    {
        var offer = _pendingOffer;
        if (offer is null) return;
        if (_settings.BiometricAccept)
        {
            if (!AndroidBiometrics.IsAvailable)
            {
                ShowToast("Biometrics are unavailable. Turn the option off in Settings → Security.",
                    IconError, error: true);
                return;
            }
            var confirmed = await AndroidBiometrics.AuthenticateAsync(
                "Accept files?", $"Confirm receiving {offer.FileCountText} from {offer.PeerName}");
            if (!confirmed)
            {
                ShowToast("Authentication cancelled. The offer is still waiting.", IconInfo);
                return;
            }
        }
        AcceptOffer(offer, OfferTrustSwitch.IsToggled);
    }

    private void AcceptOffer(TransferOffer offer, bool trust)
    {
        if (_engine is null) return;
        if (trust)
        {
            _trustStore.Trust(new PeerInfo
            {
                Id = offer.PeerId,
                Name = offer.PeerName,
                Fingerprint = offer.Fingerprint,
                Online = true
            });
            RefreshTrusted();
        }
        var item = GetOrCreateTransfer(offer.TransferId, offer.PeerName, "receive");
        item.ApplyProgress(new TransferProgress
        {
            TransferId = offer.TransferId,
            Direction = "receive",
            PeerId = offer.PeerId,
            PeerName = offer.PeerName,
            State = "waiting",
            BytesTotal = offer.TotalBytes,
            FilesTotal = offer.FileCount
        });
        UpdateTransferUi();
        _ = HideOfferAsync();
        if (!_engine.Respond(offer.OfferId, true, _settings.SaveDirectory))
        {
            item.Fail("The sender is no longer waiting.");
            ShowToast("Offer expired", IconError, error: true);
            return;
        }
        StartForegroundService($"Receiving from {offer.PeerName}");
        ShowTransferOverlay(offer.PeerName, incoming: true, "Receiving…");
        ShowToast($"Receiving from {offer.PeerName}", IconCheck);
    }

    private void DeclineOffer_Click(object sender, EventArgs e)
    {
        var offer = _pendingOffer;
        if (offer is not null) _engine?.Respond(offer.OfferId, false);
        _ = HideOfferAsync();
    }

    private async Task HideOfferAsync()
    {
        await OfferOverlay.FadeTo(0, 140, Easing.CubicIn);
        _pendingOffer = null;
        OfferOverlay.IsVisible = false;
    }

    // ── Sending ───────────────────────────────────────────────────────

    private async void SendFiles_Click(object sender, EventArgs e) => await PickAndSendAsync(null);

    private async void PeerSend_Tapped(object? sender, TappedEventArgs e)
    {
        if (e.Parameter is not string id) return;
        var peer = FindPeer(id);
        if (peer is not null) await PickAndSendAsync(peer);
    }

    private async Task PickAndSendAsync(PeerInfo? target)
    {
        if (_engine is null)
        {
            ShowToast("Wisp is offline", IconError, error: true);
            return;
        }
        try
        {
            var results = await FilePicker.Default.PickMultipleAsync(new PickOptions
            {
                PickerTitle = "Choose files to send"
            });
            if (results is null) return;
            var files = results.ToList();
            if (files.Count == 0) return;
            ShowToast("Preparing files…", IconInfo, durationSeconds: 20);
            var staged = await Task.Run(() => AndroidFileService.StageFilesAsync(files));
            if (staged.Count == 0)
            {
                ShowToast("Could not read those files", IconError, error: true);
                return;
            }
            if (target is not null)
            {
                await SendToPeerAsync(target, staged);
            }
            else
            {
                await OpenShareSheetAsync(staged);
            }
        }
        catch (Exception exception)
        {
            ShowToast($"Could not send: {exception.Message}", IconError, error: true, durationSeconds: 6);
        }
    }

    private async Task<PeerInfo?> ChoosePeerAsync()
    {
        var peers = _engine?.Peers.Where(p => p.Online).ToList() ?? [];
        if (peers.Count == 0)
        {
            ShowToast("No devices nearby. Keep Wisp open on the other device.", IconError, error: true);
            return null;
        }
        if (peers.Count == 1) return peers[0];
        var choice = await DisplayActionSheet("Send to", "Cancel", null,
            peers.Select(p => p.Name).ToArray());
        return peers.FirstOrDefault(p => p.Name == choice);
    }

    private async Task SendToPeerAsync(PeerInfo peer, IReadOnlyList<string> paths)
    {
        if (_engine is null) return;
        try
        {
            var id = await Task.Run(() => _engine.Send(paths, peer.Id));
            var item = GetOrCreateTransfer(id, peer.Name, "send");
            item.ApplyProgress(new TransferProgress
            {
                TransferId = id,
                Direction = "send",
                PeerId = peer.Id,
                PeerName = peer.Name,
                State = "preparing"
            });
            UpdateTransferUi();
            StartForegroundService($"Sending to {peer.Name}");
            ShowTransferOverlay(peer.Name, incoming: false, "Sending…");
            ShowToast($"Sending to {peer.Name}", IconCheck);
        }
        catch (Exception exception)
        {
            ShowToast($"Could not send: {exception.Message}", IconError, error: true);
        }
    }

    private async void CancelTransfer_Tapped(object? sender, TappedEventArgs e)
    {
        if (e.Parameter is not string id) return;
        if (_engine?.Cancel(id) == true) ShowToast("Cancelling…", IconInfo);
    }

    private void ClearTransfers_Click(object sender, EventArgs e)
    {
        foreach (var item in Transfers.Where(t => !t.IsActive).ToList())
        {
            Transfers.Remove(item);
            _transferById.Remove(item.Id);
        }
        UpdateTransferUi();
    }

    // ── Share inbox ───────────────────────────────────────────────────

    private void OnShareInboxChanged() => _ = ProcessSharedFilesAsync();

    private void OnAppActionChanged() => _ = ProcessAppActionsAsync();

    private async Task ProcessAppActionsAsync()
    {
        if (_engine is null || _closing) return;
        if (!AppActionInbox.DrainSend()) return;
        await PickAndSendAsync(null);
    }

    private async Task ProcessSharedFilesAsync()
    {
        if (_processingShares || _engine is null || _closing) return;
        _processingShares = true;
        try
        {
            var uris = ShareInbox.Drain();
            if (uris.Count == 0) return;
            ShowToast("Preparing shared files…", IconInfo, durationSeconds: 20);
            var staged = await Task.Run(() => AndroidFileService.StageUrisAsync(uris));
            if (staged.Count == 0)
            {
                ShowToast("Could not read the shared files", IconError, error: true);
                return;
            }
            await OpenShareSheetAsync(staged);
        }
        catch (Exception exception)
        {
            ShowToast($"Could not open the shared files: {exception.Message}", IconError, error: true, durationSeconds: 6);
        }
        finally
        {
            _processingShares = false;
        }
    }

    // ── Navigation with motion ────────────────────────────────────────

    private void NavNearby_Tapped(object? sender, TappedEventArgs e) => SetSection("nearby");
    private void NavTransfers_Tapped(object? sender, TappedEventArgs e) => SetSection("transfers");
    private void NavFiles_Tapped(object? sender, TappedEventArgs e) => SetSection("files");
    private void NavSettings_Tapped(object? sender, TappedEventArgs e) => SetSection("settings");
    private void HeaderActivity_Tapped(object? sender, TappedEventArgs e) => SetSection("transfers");

    private void SetSectionInitial()
    {
        _activeSection = "nearby";
        NearbyScroll.IsVisible = true;
        TransfersScroll.IsVisible = false;
        FilesScroll.IsVisible = false;
        SettingsScroll.IsVisible = false;
        StyleNav(NavNearbyPill, NavNearbyIcon, NavNearbyText, true);
        StyleNav(NavTransfersPill, NavTransfersIcon, NavTransfersText, false);
        StyleNav(NavFilesPill, NavFilesIcon, NavFilesText, false);
        StyleNav(NavSettingsPill, NavSettingsIcon, NavSettingsText, false);
    }

    private void SetSection(string section)
    {
        if (_activeSection == section) return;
        if (!_navigatingBack)
        {
            // Nearby is the home tab; tapping it resets the back stack. Other
            // tabs remember where they came from so back returns to that tab.
            if (section == "nearby")
            {
                _sectionHistory.Clear();
            }
            else
            {
                _sectionHistory.Remove(section);
                _sectionHistory.Add(_activeSection);
            }
        }
        _activeSection = section;
        var incoming = section == "nearby" ? (VisualElement)NearbyScroll
            : section == "transfers" ? (VisualElement)TransfersScroll
            : section == "files" ? (VisualElement)FilesScroll
            : (VisualElement)SettingsScroll;
        NearbyScroll.IsVisible = section == "nearby";
        TransfersScroll.IsVisible = section == "transfers";
        FilesScroll.IsVisible = section == "files";
        SettingsScroll.IsVisible = section == "settings";
        incoming.Opacity = 0;
        incoming.TranslationY = 14;
        _ = incoming.FadeTo(1, 200, Easing.CubicOut);
        _ = incoming.TranslateTo(0, 0, 220, Easing.CubicOut);
        StyleNav(NavNearbyPill, NavNearbyIcon, NavNearbyText, section == "nearby");
        StyleNav(NavTransfersPill, NavTransfersIcon, NavTransfersText, section == "transfers");
        StyleNav(NavFilesPill, NavFilesIcon, NavFilesText, section == "files");
        StyleNav(NavSettingsPill, NavSettingsIcon, NavSettingsText, section == "settings");
        if (section == "nearby") _ = ProcessSharedFilesAsync();
        if (section == "transfers") RefreshHistory();
        if (section == "files") _ = RefreshLibraryAsync();
    }

    private void StyleNav(Border pill, Label icon, Label text, bool selected)
    {
        var onAccent = MauiColor.FromArgb("#08120F");
        var lightTheme = Application.Current?.RequestedTheme == AppTheme.Light;
        pill.Background = selected
            ? new LinearGradientBrush(
                new GradientStopCollection
                {
                    new GradientStop(MauiColor.FromArgb("#45E3B4"), 0f),
                    new GradientStop(MauiColor.FromArgb("#5B8DEF"), 1f)
                },
                new Point(0, 0), new Point(1, 1))
            : new SolidColorBrush(Colors.Transparent);
        icon.TextColor = selected ? onAccent : (lightTheme ? FaintLightColor : FaintDarkColor);
        text.TextColor = selected ? onAccent : (lightTheme ? FaintLightColor : FaintDarkColor);
        if (selected)
        {
            _ = pill.ScaleTo(0.96, 90, Easing.CubicOut)
                .ContinueWith(_ => MainThread.BeginInvokeOnMainThread(
                    () => _ = pill.ScaleTo(1.0, 130, Easing.SpringOut)));
        }
    }

    // ── Settings actions ──────────────────────────────────────────────

    private async void ApplySettings_Click(object sender, EventArgs e)
    {
        var name = DeviceNameEntry.Text?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            ShowToast("Give this device a name first", IconError, error: true);
            return;
        }
        if (HasActiveTransfers())
        {
            ShowToast("Finish or cancel the current transfer first.", IconInfo);
            return;
        }
        _settings.DeviceName = name;
        _settings.AutoAcceptTrusted = AutoAcceptSwitch.IsToggled;
        _settings.SoundsEnabled = SoundsSwitch.IsToggled;
        _settings.NotificationsEnabled = NotificationsSwitch.IsToggled;
        _settingsStore.Save(_settings);
        SettingsStatusLabel.Text = "Restarting…";
        if (_temporaryLinkActive || _temporaryHotspot is not null || _temporaryWifiLink is not null)
        {
            // Restarting the engine would invalidate the hotspot token and
            // source binding, so close the temporary link cleanly first.
            await DisconnectTemporaryLinkAsync(restartEngine: false, notify: true);
            await WaitForWifiRestoreAsync(TimeSpan.FromSeconds(8));
        }
        await StartEngineAsync();
        SettingsStatusLabel.Text = "Saved.";
        ShowToast("Settings saved", IconCheck);
    }

    private void ThemePill_Tapped(object? sender, TappedEventArgs e)
    {
        if (e.Parameter is not string theme) return;
        if (_settings.Theme == theme) return;
        _settings.Theme = theme;
        _settingsStore.Save(_settings);
        ((App)Application.Current!).ApplyTheme(theme);
        UpdateThemePills();
        AndroidTheme.Apply(theme == "light"
            || (theme == "system" && Application.Current?.RequestedTheme == AppTheme.Light));
        if (SettingsDetailHeader.IsVisible) SetSettingsCategory(_settingsCategory);
        ShowToast($"Theme: {theme}", IconInfo);
    }

    private void UpdateThemePills()
    {
        var theme = _settings.Theme;
        StyleThemePill(ThemeSystemPill, ThemeSystemText, theme == "system");
        StyleThemePill(ThemeLightPill, ThemeLightText, theme == "light");
        StyleThemePill(ThemeDarkPill, ThemeDarkText, theme == "dark");
    }

    private static void StyleThemePill(Border pill, Label label, bool selected)
    {
        var lightTheme = Application.Current?.RequestedTheme == AppTheme.Light;
        pill.BackgroundColor = selected ? MauiColor.FromArgb("#1F45E3B4") : Colors.Transparent;
        pill.Stroke = new SolidColorBrush(selected
            ? MintColor
            : (lightTheme ? MauiColor.FromArgb("#E2E8E5") : MauiColor.FromArgb("#1FFFFFFF")));
        label.TextColor = selected ? MintColor
            : (lightTheme ? MauiColor.FromArgb("#5B6663") : MauiColor.FromArgb("#9AA6A3"));
    }

    private void SoundsToggled(object? sender, ToggledEventArgs e)
    {
        _settings.SoundsEnabled = e.Value;
        _settingsStore.Save(_settings);
        SoundService.SetEnabled(e.Value);
        if (e.Value) SoundService.Play("connect");
    }

    private void NotificationsToggled(object? sender, ToggledEventArgs e)
    {
        _settings.NotificationsEnabled = e.Value;
        _settingsStore.Save(_settings);
        if (e.Value) RequestNotificationPermission();
    }

    private void AutoAcceptToggled(object? sender, ToggledEventArgs e)
    {
        _settings.AutoAcceptTrusted = e.Value;
        _settingsStore.Save(_settings);
    }

    private async void ShowIdentity_Click(object sender, EventArgs e)
    {
        var self = _engine?.Self;
        if (self is null)
        {
            ShowToast("Wisp is offline", IconError, error: true);
            return;
        }
        var choice = await DisplayActionSheet(
            $"{self.Name}\nPeer ID {self.Id}\nPort {self.Port}\nFingerprint {self.Fingerprint}",
            "Close", null, "Copy peer ID", "Copy fingerprint");
        if (choice == "Copy peer ID") await Clipboard.Default.SetTextAsync(self.Id);
        else if (choice == "Copy fingerprint") await Clipboard.Default.SetTextAsync(self.Fingerprint);
        else return;
        ShowToast("Copied to clipboard", IconCheck);
    }

    private void GrantPermissions_Click(object sender, EventArgs e)
    {
        AndroidPermissions.RequestAll(force: true);
        ShowToast("Review the permission prompts to keep Wisp fully local", IconInfo);
    }

    private void OpenAppSettings_Click(object sender, EventArgs e)
    {
        try
        {
            AppInfo.Current.ShowSettingsUI();
        }
        catch
        {
            ShowToast("Open Android Settings > Apps > Wisp to manage permissions", IconInfo);
        }
    }

    private async void CopyPeerId_Click(object sender, EventArgs e)
    {
        var id = _engine?.Self?.Id;
        if (string.IsNullOrEmpty(id)) return;
        await Clipboard.Default.SetTextAsync(id);
        ShowToast("Peer ID copied", IconCheck);
    }

    private async void OpenSourceCode_Click(object sender, EventArgs e) =>
        await OpenExternalAsync("https://github.com/sudaissalisu/wisp");

    private async void OpenLicense_Click(object sender, EventArgs e) =>
        await OpenExternalAsync("https://github.com/sudaissalisu/wisp/blob/main/LICENSE");

    private async Task OpenExternalAsync(string url)
    {
        try
        {
            await Launcher.Default.OpenAsync(url);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Could not open {url}: {exception.Message}");
            ShowToast("No browser is available on this device.", IconInfo);
        }
    }

    private async void ForgetTrusted_Tapped(object? sender, TappedEventArgs e)
    {
        if (e.Parameter is not string fingerprint) return;
        var confirm = await DisplayAlert("Forget device?",
            "This device will need to pair again before you can trust it.", "Forget", "Cancel");
        if (!confirm) return;
        _trustStore.Remove(fingerprint);
        RefreshTrusted();
    }

    private void RefreshTrusted()
    {
        Trusted.Clear();
        foreach (var peer in _trustStore.Items) Trusted.Add(new TrustedItem(peer));
        TrustedEmpty.IsVisible = Trusted.Count == 0 && SecurityCard.IsVisible;
        TrustedHost.IsVisible = Trusted.Count > 0 && SecurityCard.IsVisible;
        TrustedCountLabel.Text = Trusted.Count == 0 ? "" :
            Trusted.Count == 1 ? "1 device" : $"{Trusted.Count} devices";
    }

    // ── AirDrop-style send sheet ──────────────────────────────────────

    private async Task OpenShareSheetAsync(IReadOnlyList<string> paths)
    {
        _pendingSharePaths.Clear();
        _pendingSharePaths.AddRange(paths);
        var total = 0L;
        foreach (var path in paths)
        {
            try { total += new FileInfo(path).Length; } catch { }
        }
        ShareSummaryLabel.Text = $"{paths.Count} item(s) · {TransferOffer.FormatBytes(total)}";
        ShareFilesLabel.Text = string.Join(System.Environment.NewLine, paths.Select(Path.GetFileName).Take(4));
        ShareSheetOverlay.IsVisible = true;
        ShareSheetOverlay.Opacity = 0;
        UpdateShareSheetState();
        await ShareSheetOverlay.FadeTo(1, 170, Easing.CubicOut);
        await EnsureDiscoveryHintAsync();
        UpdateShareSheetState();
    }

    private void UpdateShareSheetState()
    {
        try
        {
            var hasPeers = Peers.Count > 0;
            var visible = ShareSheetOverlay.IsVisible;
            SharePeersHost.IsVisible = hasPeers;
            ShareEmptyLabel.IsVisible = !hasPeers;
            ShareQrBlock.IsVisible = !hasPeers && visible && !_temporaryLinkActive;
            ShareScanSpinner.IsRunning = !hasPeers && visible;
        }
        catch
        {
        }
    }

    private async void CloseShareSheet_Click(object sender, EventArgs e)
    {
        _pendingSharePaths.Clear();
        await ShareSheetOverlay.FadeTo(0, 120, Easing.CubicIn);
        ShareSheetOverlay.IsVisible = false;
        ShareScanSpinner.IsRunning = false;
    }

    private async void ShareQrButton_Click(object? sender, EventArgs e)
    {
        if (_pendingSharePaths.Count > 0)
        {
            // Keep the staged files and send them automatically once the
            // scanned link is connected.
            _linkSendAfterConnect = _pendingSharePaths.ToArray();
            _pendingSharePaths.Clear();
            await ShareSheetOverlay.FadeTo(0, 120, Easing.CubicIn);
            ShareSheetOverlay.IsVisible = false;
            ShareScanSpinner.IsRunning = false;
        }
        await ScanTemporaryLinkAsync();
    }

    private async void SharePeer_Tapped(object? sender, TappedEventArgs e)
    {
        if (e.Parameter is not string id) return;
        var peer = FindPeer(id);
        if (peer is null || _pendingSharePaths.Count == 0) return;
        var paths = _pendingSharePaths.ToArray();
        _pendingSharePaths.Clear();
        await ShareSheetOverlay.FadeTo(0, 120, Easing.CubicIn);
        ShareSheetOverlay.IsVisible = false;
        ShareScanSpinner.IsRunning = false;
        await SendToPeerAsync(peer, paths);
    }

    private PeerInfo? FindPeer(string id)
    {
        var peer = _engine?.Peers.FirstOrDefault(item =>
            item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (peer is not null) return peer;
        // The host added through a QR link is a manual peer and may not appear
        // in native discovery results.
        if (_linkPeer is not null && _linkPeer.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            return _linkPeer;
        return _peerById.TryGetValue(id, out var item2) ? item2.Info : null;
    }

    private async Task EnsureDiscoveryHintAsync()
    {
        // If the app was just launched from a share intent, discovery may not
        // have produced a peer yet. Give the sweep a moment, then keep the sheet
        // open so later peer events populate it live.
        if (_engine is null) return;
        for (var i = 0; i < 8 && Peers.Count == 0; i++)
        {
            await Task.Delay(250);
            if (Peers.Count > 0) break;
        }
        UpdateShareSheetState();
    }

    // ── Live transfer sheet ───────────────────────────────────────────

    private void ShowTransferOverlay(string peerName, bool incoming, string state)
    {
        if (string.IsNullOrEmpty(_activeTransferId) && Transfers.Count > 0)
            _activeTransferId = Transfers.FirstOrDefault(t => t.IsActive)?.Id;
        var existingProgress = 0.0;
        if (!string.IsNullOrEmpty(_activeTransferId) &&
            _transferById.TryGetValue(_activeTransferId, out var existing))
        {
            existingProgress = existing.Progress;
        }
        TransferPeerLabel.Text = incoming ? $"Receiving from {peerName}" : $"Sending to {peerName}";
        TransferGlyph.Text = incoming ? "" : "";
        TransferStateLabel.Text = existingProgress > 0 ? "Transferring…" : state;
        TransferProgressBar.Progress = existingProgress;
        TransferPercentLabel.Text = $"{existingProgress * 100:0}%";
        TransferSpeedLabel.Text = "";
        UpdateMiniDock(incoming, peerName, existingProgress,
            existingProgress > 0 ? "Transferring…" : state);
        _transferMinimized = false;
        HideMiniDock();
        TransferOverlay.IsVisible = true;
        TransferOverlay.Opacity = 0;
        _ = TransferOverlay.FadeTo(1, 160, Easing.CubicOut);
    }

    private void UpdateTransferOverlay(TransferProgress progress)
    {
        if (progress.BytesTotal <= 0) return;
        if (string.IsNullOrEmpty(_activeTransferId) ||
            _activeTransferId.Equals(progress.TransferId, StringComparison.OrdinalIgnoreCase))
        {
            _activeTransferId = progress.TransferId;
        }
        var pct = Math.Clamp(progress.Fraction, 0, 1);
        TransferPeerLabel.Text = progress.IsIncoming
            ? $"Receiving from {progress.PeerName}" : $"Sending to {progress.PeerName}";
        TransferGlyph.Text = progress.IsIncoming ? "" : "";
        TransferStateLabel.Text = progress.State switch
        {
            "preparing" => "Preparing files…",
            "waiting" => "Waiting for the other device…",
            _ => $"{progress.FilesDone}/{progress.FilesTotal} files · {progress.SpeedText}"
        };
        TransferProgressBar.Progress = pct;
        TransferPercentLabel.Text = $"{pct * 100:0}%";
        TransferSpeedLabel.Text = progress.BytesDone > 0
            ? $"{TransferOffer.FormatBytes(progress.BytesDone)} of {TransferOffer.FormatBytes(progress.BytesTotal)}"
            : "";
        UpdateMiniDock(progress.IsIncoming, progress.PeerName, pct,
            progress.State switch
            {
                "preparing" => "Preparing files",
                "waiting" => $"Waiting for {progress.PeerName}",
                _ => $"{progress.FilesDone}/{progress.FilesTotal} files · {progress.SpeedText}"
            });
        TransferActionButton.Text = "Cancel";
        if (!_transferMinimized) TransferOverlay.IsVisible = true;
        else ShowMiniDock();
    }

    private void FinishTransferOverlay(string transferId, bool success)
    {
        if (!string.IsNullOrEmpty(_activeTransferId) &&
            !_activeTransferId.Equals(transferId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        if (success)
        {
            TransferProgressBar.Progress = 1;
            TransferPercentLabel.Text = "100%";
            MiniProgressBar.Progress = 1;
            MiniProgressLabel.Text = "100%";
            MiniProgressGlyph.Text = "\uE86C";
            MiniProgressStatusLabel.Text = "Complete";
            MiniProgressCancelButton.IsVisible = false;
        }
        else
        {
            TransferPercentLabel.Text = "—";
            MiniProgressLabel.Text = "!";
            MiniProgressGlyph.Text = "\uE001";
            MiniProgressStatusLabel.Text = "Stopped";
            MiniProgressCancelButton.IsVisible = false;
        }
        TransferSpeedLabel.Text = "";
        TransferStateLabel.Text = success ? "Complete" : "Stopped";
        TransferActionButton.Text = "Done";
        _activeTransferId = null;
        if (!_transferMinimized)
        {
            TransferOverlay.IsVisible = true;
        }
        else
        {
            ShowMiniDock();
            ScheduleMiniDockHide(success ? 3200 : 4800);
        }
    }

    private void MinimizeTransfer_Click(object sender, EventArgs e)
    {
        _transferMinimized = true;
        TransferOverlay.IsVisible = false;
        ShowMiniDock();
    }

    private void RestoreTransferOverlay_Tapped(object? sender, TappedEventArgs e)
    {
        _transferMinimized = false;
        HideMiniDock();
        TransferOverlay.IsVisible = true;
    }

    private void TransferAction_Click(object sender, EventArgs e)
    {
        if (!string.IsNullOrEmpty(_activeTransferId))
        {
            _engine?.Cancel(_activeTransferId);
            return;
        }
        TransferOverlay.IsVisible = false;
        HideMiniDock();
        _transferMinimized = false;
    }

    private void UpdateMiniDock(bool incoming, string peerName, double progress, string status)
    {
        MiniProgressTitleLabel.Text = incoming
            ? $"Receiving from {peerName}"
            : $"Sending to {peerName}";
        var fraction = Math.Clamp(progress, 0, 1);
        MiniProgressLabel.Text = $"{fraction * 100:0}%";
        MiniProgressBar.Progress = fraction;
        MiniProgressGlyph.Text = incoming ? "\uE896" : "\uE898";
        MiniProgressStatusLabel.Text = status;
        MiniProgressCancelButton.IsVisible = true;
    }

    private void ShowMiniDock()
    {
        CancelMiniDockHide();
        if (MiniProgressPill.IsVisible) return;
        _miniDockToken++;
        MiniProgressPill.Opacity = 0;
        MiniProgressPill.TranslationY = 16;
        MiniProgressPill.IsVisible = true;
        _ = MiniProgressPill.FadeTo(1, 160, Easing.CubicOut);
        _ = MiniProgressPill.TranslateTo(0, 0, 200, Easing.CubicOut);
    }

    private void HideMiniDock()
    {
        CancelMiniDockHide();
        if (!MiniProgressPill.IsVisible) return;
        var token = ++_miniDockToken;
        _ = HideMiniDockAsync(token);
    }

    private async Task HideMiniDockAsync(int token)
    {
        await MiniProgressPill.FadeTo(0, 130, Easing.CubicIn);
        if (token != _miniDockToken) return;
        MiniProgressPill.IsVisible = false;
        MiniProgressPill.Opacity = 1;
        MiniProgressPill.TranslationY = 0;
    }

    private void ScheduleMiniDockHide(int milliseconds)
    {
        CancelMiniDockHide();
        var source = new CancellationTokenSource();
        _miniDockHide = source;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(milliseconds, source.Token);
            }
            catch (TaskCanceledException)
            {
                return;
            }
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (!source.IsCancellationRequested) HideMiniDock();
            });
        });
    }

    private void CancelMiniDockHide()
    {
        try { _miniDockHide?.Cancel(); } catch { }
        _miniDockHide = null;
    }

    private void MiniProgressCancel_Click(object? sender, EventArgs e)
    {
        if (string.IsNullOrEmpty(_activeTransferId)) return;
        if (_engine?.Cancel(_activeTransferId) == true)
            ShowToast("Cancelling…", IconInfo);
    }

    // ── Temporary QR link ────────────────────────────────────────────

    private async void CreateTemporaryLink_Click(object? sender, EventArgs e)
    {
        if (_temporaryLinkBusy) return;
        if (HasActiveTransfers())
        {
            ShowToast("Finish or cancel the current transfer first.", IconInfo);
            return;
        }
        if (_temporaryLinkActive || _temporaryHotspot is not null || _temporaryWifiLink is not null)
        {
            ShowLinkOverlay();
            return;
        }
        if (_engine is null)
        {
            ShowToast("Wisp is offline. Try again in a moment.", IconError, error: true);
            return;
        }
        if (Build.VERSION.SdkInt < BuildVersionCodes.O)
        {
            ShowToast("Temporary links need Android 8 or newer.", IconError, error: true);
            return;
        }

        _temporaryLinkBusy = true;
        try
        {
            if (!await EnsureLinkPermissionsAsync(needCamera: false)) return;
            _temporaryLinkRole = "host";
            _activeLink = null;
            _linkPeer = null;
            _linkPeerJoined = false;
            _temporaryLinkClosing = false;
            ShowLinkOverlay();
            LinkSubtitleLabel.Text = "Creating a private hotspot";
            ShowLinkPanels(connecting: true);
            LinkConnectingLabel.Text = "Starting a private hotspot…";
            LinkConnectingPanel.IsVisible = true;

            var hotspot = new TemporaryHotspot();
            hotspot.Started += credentials => OnTemporaryHotspotStarted(hotspot, credentials);
            hotspot.Failed += message => OnTemporaryHotspotFailed(hotspot, message);
            hotspot.Stopped += () => OnTemporaryHotspotStopped(hotspot);
            _temporaryHotspot = hotspot;
            hotspot.Start();
        }
        catch (Exception exception)
        {
            ShowToast($"Could not create a temporary link: {exception.Message}",
                IconError, error: true, durationSeconds: 7);
            await DisconnectTemporaryLinkAsync(restartEngine: true, notify: false);
        }
        finally
        {
            _temporaryLinkBusy = false;
        }
    }

    private async void ScanTemporaryLink_Click(object? sender, EventArgs e) =>
        await ScanTemporaryLinkAsync();

    private async Task ScanTemporaryLinkAsync()
    {
        if (_temporaryLinkBusy) return;
        if (HasActiveTransfers())
        {
            _linkSendAfterConnect = null;
            ShowToast("Finish or cancel the current transfer first.", IconInfo);
            return;
        }
        if (_temporaryLinkActive || _temporaryHotspot is not null || _temporaryWifiLink is not null)
        {
            _linkSendAfterConnect = null;
            ShowLinkOverlay();
            return;
        }
        if (Build.VERSION.SdkInt < BuildVersionCodes.Q)
        {
            _linkSendAfterConnect = null;
            ShowToast("Joining a temporary link needs Android 10 or newer.", IconError, error: true);
            return;
        }

        _temporaryLinkBusy = true;
        try
        {
            if (!await EnsureLinkPermissionsAsync(needCamera: true))
            {
                _linkSendAfterConnect = null;
                return;
            }
            if (!BarcodeScanning.IsSupported)
            {
                _linkSendAfterConnect = null;
                ShowToast("This phone has no usable camera for QR scanning.",
                    IconError, error: true);
                return;
            }
            _temporaryLinkRole = "scan";
            _activeLink = null;
            _linkPeer = null;
            _linkPeerJoined = false;
            _temporaryLinkClosing = false;
            _scanPending = false;
            ShowLinkOverlay();
            LinkSubtitleLabel.Text = "Scan the other phone's code";
            ShowLinkPanels(scan: true);
            StartScannerDetection();
            UpdateLinkCard();
        }
        catch (Exception exception)
        {
            _linkSendAfterConnect = null;
            ShowToast($"Could not open the scanner: {exception.Message}",
                IconError, error: true);
            HideLinkOverlay();
        }
        finally
        {
            _temporaryLinkBusy = false;
        }
    }

    private async void CloseTemporaryLink_Click(object? sender, EventArgs e)
    {
        // The close button only hides the sheet. A link that is already up
        // keeps running so more files can be sent; ending it is explicit.
        if (_temporaryLinkActive)
        {
            HideLinkOverlay();
            ShowToast("Temporary link stays active. Use Disconnect to end it.", IconInfo);
            return;
        }
        if (_temporaryHotspot is not null || _temporaryWifiLink is not null)
        {
            await DisconnectTemporaryLinkAsync(restartEngine: true, notify: false);
            return;
        }
        _linkSendAfterConnect = null;
        StopScannerDetection();
        HideLinkOverlay();
    }

    private void OpenTemporaryLink_Click(object? sender, EventArgs e)
    {
        if (!_temporaryLinkActive) return;
        ShowLinkOverlay();
    }

    private async void DisconnectTemporaryLink_Click(object? sender, EventArgs e)
    {
        await DisconnectTemporaryLinkAsync(restartEngine: true, notify: true, confirm: HasActiveTransfers());
    }

    private async void LinkSendFiles_Click(object? sender, EventArgs e)
    {
        if (_engine is null)
        {
            ShowToast("Wisp is offline", IconError, error: true);
            return;
        }
        PeerInfo? target = _linkPeer;
        if (target is null && _activeLink is not null)
        {
            target = _engine.Peers.FirstOrDefault(peer =>
                peer.Fingerprint.Equals(_activeLink.Fingerprint, StringComparison.OrdinalIgnoreCase) ||
                (peer.Host == _activeLink.Host && peer.Port == _activeLink.Port));
        }
        if (target is null && _temporaryLinkRole == "host" && _activeLink is not null &&
            _activeLink.Host.LastIndexOf('.') > 0)
        {
            var subnet = _activeLink.Host[..(_activeLink.Host.LastIndexOf('.') + 1)];
            var candidates = _engine.Peers
                .Where(peer => peer.Online && peer.Host.StartsWith(subnet, StringComparison.Ordinal))
                .ToList();
            if (candidates.Count > 0) target = candidates[0];
            else
            {
                ShowToast("The other phone has not appeared on the private link yet. It can send to you now; wait a moment before sending back.",
                    IconInfo, durationSeconds: 7);
                return;
            }
        }
        await PickAndSendAsync(target);
    }

    private void OnTemporaryHotspotStarted(TemporaryHotspot source, HotspotCredentials credentials)
    {
        if (_closing || _temporaryLinkClosing ||
            !ReferenceEquals(source, _temporaryHotspot)) return;
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            try
            {
                _temporaryLinkRole = "host";
                _temporaryLinkActive = true;
                _linkPeerJoined = false;
                LinkConnectingLabel.Text = "Preparing the transfer engine…";
                // The old engine was pinned to the home Wi-Fi, which may be
                // unavailable while the soft AP is active. Start a fresh
                // listener without a process-wide Wi-Fi binding; the accepted
                // sockets arrive on the hotspot interface.
                if (!await StartEngineAsync(bindWifi: false, discovery: true, announce: false))
                {
                    await FailTemporaryLinkAsync("The transfer engine could not start for the temporary link.");
                    return;
                }
                if (_closing || _temporaryLinkClosing ||
                    !ReferenceEquals(source, _temporaryHotspot)) return;
                var engine = _engine;
                if (engine is null) return;
                var (host, usedFallback) = await ResolveHotspotHostAsync();
                if (_closing || _temporaryLinkClosing ||
                    !ReferenceEquals(source, _temporaryHotspot)) return;
                var token = NewLinkToken();
                if (!engine.SetExpectedLinkToken(token))
                {
                    await FailTemporaryLinkAsync("Could not arm the one-time link token.");
                    return;
                }
                // The soft AP is not a ConnectivityManager network, so pin the
                // host's outgoing transfer sockets to its hotspot address.
                engine.SetBindAddress(host);
                var payload = QrLinkPayload.Create(
                    credentials.Ssid,
                    credentials.Passphrase,
                    host,
                    engine.ListenPort,
                    engine.Fingerprint,
                    token,
                    _settings.DeviceName,
                    TemporaryLinkLifetime);
                _activeLink = payload;
                _linkPeer = null;
                LinkQrImage.Source = QrCodeRenderer.Create(payload.ToUriString());
                LinkSsidLabel.Text = payload.Ssid;
                LinkPassphraseLabel.Text = $"Password {payload.Passphrase}";
                LinkEndpointLabel.Text = $"{payload.EndpointText} · pinned {ShortFingerprint(payload.Fingerprint)}";
                LinkSubtitleLabel.Text = "Scan this code on the other phone";
                LinkExpiryLabel.Text = "Preparing expiry…";
                ShowLinkPanels(host: true);
                StartLinkExpiryTimer(payload);
                UpdateLinkCard();
                UpdatePeerUi();
                ShowToast(usedFallback
                        ? "Temporary link ready (using the standard hotspot address)"
                        : "Temporary link ready", IconCheck, durationSeconds: 5);
                if (usedFallback)
                {
                    ShowToast("Could not detect the hotspot IP; the code uses 192.168.43.1. If scanning fails, use a shared Wi-Fi network instead.",
                        IconInfo, durationSeconds: 10);
                }
            }
            catch (Exception exception)
            {
                await FailTemporaryLinkAsync($"Could not prepare the temporary link: {exception.Message}");
            }
        });
    }

    private void OnTemporaryHotspotFailed(TemporaryHotspot source, string message)
    {
        if (_closing || _temporaryLinkClosing ||
            !ReferenceEquals(source, _temporaryHotspot)) return;
        _ = FailTemporaryLinkAsync(message);
    }

    private void OnTemporaryHotspotStopped(TemporaryHotspot source)
    {
        if (_closing || _temporaryLinkClosing ||
            !ReferenceEquals(source, _temporaryHotspot)) return;
        ShowToast("The temporary hotspot stopped.", IconInfo);
        _ = DisconnectTemporaryLinkAsync(restartEngine: true, notify: false);
    }

    private void Scanner_BarcodesDetected(object? sender, BarcodeDetectionEventArgs e)
    {
        if (_scannerHandling) return;
        var result = e.Results?.FirstOrDefault(item => item.Format == BarcodeFormat.QrCode)
                     ?? e.Results?.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(result?.Value)) return;
        _scannerHandling = true;
        var value = result.Value;
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            try
            {
                if (!LinkScanPanel.IsVisible || _temporaryLinkActive)
                {
                    _scannerHandling = false;
                    return;
                }
                StopScannerDetection();
                await JoinTemporaryLinkAsync(value);
            }
            finally
            {
                _scannerHandling = false;
            }
        });
    }

    private async Task JoinTemporaryLinkAsync(string rawValue)
    {
        if (!QrLinkPayload.TryParse(rawValue, out var payload, out var error) || payload is null)
        {
            LinkScanStatusLabel.Text = error;
            ShowToast(error, IconError, error: true);
            await Task.Delay(1200);
            if (LinkScanPanel.IsVisible && !_temporaryLinkActive && !_temporaryLinkClosing)
            {
                StartScannerDetection();
            }
            return;
        }

        _temporaryLinkRole = "scan";
        _activeLink = payload;
        _linkPeer = null;
        _linkPeerJoined = false;
        LinkSubtitleLabel.Text = $"Connecting to {payload.DeviceName}";
        ShowLinkPanels(connecting: true);
        LinkConnectingLabel.Text = $"Joining {payload.Ssid}…";

        var wifiLink = new TemporaryWifiLink();
        wifiLink.Connected += network => OnTemporaryWifiConnected(wifiLink, network);
        wifiLink.Failed += message => OnTemporaryWifiFailed(wifiLink, message);
        wifiLink.Lost += () => OnTemporaryWifiLost(wifiLink);
        _temporaryWifiLink = wifiLink;
        if (!wifiLink.Join(payload.Ssid, payload.Passphrase))
        {
            await FailTemporaryLinkAsync("Could not join the temporary Wi-Fi.");
        }
    }

    private void OnTemporaryWifiFailed(TemporaryWifiLink source, string message)
    {
        if (_closing || _temporaryLinkClosing ||
            !ReferenceEquals(source, _temporaryWifiLink)) return;
        _ = FailTemporaryLinkAsync(message);
    }

    private void OnTemporaryWifiLost(TemporaryWifiLink source)
    {
        if (_closing || _temporaryLinkClosing ||
            !ReferenceEquals(source, _temporaryWifiLink)) return;
        ShowToast("The temporary Wi-Fi link was lost.", IconInfo);
        _ = DisconnectTemporaryLinkAsync(restartEngine: true, notify: false);
    }

    private void OnTemporaryWifiConnected(TemporaryWifiLink source, Network network)
    {
        if (_closing || _temporaryLinkClosing ||
            !ReferenceEquals(source, _temporaryWifiLink)) return;
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            var payload = _activeLink;
            if (payload is null || _temporaryLinkRole != "scan" ||
                !ReferenceEquals(source, _temporaryWifiLink)) return;
            try
            {
                LinkConnectingLabel.Text = "Adding the other phone as a pinned peer…";
                // The process is already bound to the temporary network by
                // TemporaryWifiLink, so clearBindings must stay false.
                if (!await StartEngineAsync(bindWifi: false, discovery: true,
                        announce: false, clearBindings: false))
                {
                    await FailTemporaryLinkAsync("The transfer engine could not start on the temporary link.");
                    return;
                }
                if (_closing || _temporaryLinkClosing ||
                    !ReferenceEquals(source, _temporaryWifiLink)) return;
                var engine = _engine;
                if (engine is null) return;
                if (!engine.SetLinkToken(payload.Token))
                {
                    await FailTemporaryLinkAsync("Could not apply the one-time link token.");
                    return;
                }
                var peer = new PeerInfo
                {
                    Name = payload.DeviceName,
                    Platform = "android",
                    Host = payload.Host,
                    Port = payload.Port,
                    Fingerprint = payload.Fingerprint,
                    Online = true
                };
                if (!engine.AddPeer(peer))
                {
                    await FailTemporaryLinkAsync("The host address from the QR code was rejected.");
                    return;
                }
                _linkPeer = peer;
                _temporaryLinkActive = true;
                _linkPeerJoined = true;
                StopLinkExpiryTimer();
                ApplyPeer(new PeerEventArgs(peer, true));
                LinkSubtitleLabel.Text = $"Connected to {payload.DeviceName}";
                LinkConnectedLabel.Text = $"Connected to {payload.DeviceName}";
                LinkConnectedEndpointLabel.Text =
                    $"{payload.EndpointText} · pinned {ShortFingerprint(payload.Fingerprint)}";
                ShowLinkPanels(connected: true);
                UpdateLinkCard();
                UpdatePeerUi();
                ShowToast($"Connected to {payload.DeviceName} over a private link",
                    IconCheck, durationSeconds: 6);
                var queued = _linkSendAfterConnect;
                _linkSendAfterConnect = null;
                if (queued is { Length: > 0 })
                {
                    HideLinkOverlay();
                    ShowToast($"Connected. Sending {queued.Length} staged file(s)…",
                        IconCheck, durationSeconds: 6);
                    await SendToPeerAsync(peer, queued);
                }
            }
            catch (Exception exception)
            {
                await FailTemporaryLinkAsync($"Could not use the temporary link: {exception.Message}");
            }
        });
    }

    private async Task FailTemporaryLinkAsync(string message)
    {
        if (_closing || _temporaryLinkClosing) return;
        ShowToast(message, IconError, error: true, durationSeconds: 8);
        if (LinkScanPanel.IsVisible) LinkScanStatusLabel.Text = message;
        await DisconnectTemporaryLinkAsync(restartEngine: true, notify: false);
    }

    private async Task DisconnectTemporaryLinkAsync(bool restartEngine, bool notify,
        bool confirm = false)
    {
        if (_temporaryLinkClosing) return;
        if (confirm && HasActiveTransfers())
        {
            var proceed = await DisplayAlertAsync("Disconnect the temporary link?",
                "An active transfer will stop.", "Disconnect", "Keep link");
            if (!proceed) return;
        }
        _temporaryLinkClosing = true;
        try
        {
            StopLinkExpiryTimer();
            StopScannerDetection();
            _scanPending = false;

            var hotspot = _temporaryHotspot;
            _temporaryHotspot = null;
            var wifiLink = _temporaryWifiLink;
            _temporaryWifiLink = null;
            try
            {
                hotspot?.Stop();
                hotspot?.Dispose();
            }
            catch
            {
            }
            try
            {
                wifiLink?.Disconnect();
                wifiLink?.Dispose();
            }
            catch
            {
            }

            var engine = _engine;
            try
            {
                engine?.SetExpectedLinkToken("");
                engine?.SetLinkToken("");
            }
            catch
            {
            }

            _temporaryLinkActive = false;
            _temporaryLinkRole = "";
            _activeLink = null;
            _linkPeer = null;
            _linkPeerJoined = false;
            _linkSendAfterConnect = null;
            LinkHostHintLabel.Text = "Waiting for the other phone to scan this code…";
            HideLinkOverlay();
            UpdateLinkCard();
            UpdatePeerUi();

            if (restartEngine && !_closing)
            {
                if (notify) ShowToast("Closing the temporary link and restoring Wi-Fi…", IconInfo);
                await WaitForWifiRestoreAsync(TimeSpan.FromSeconds(8));
                if (!_closing)
                {
                    var started = await StartEngineAsync(bindWifi: true, discovery: true,
                        announce: false);
                    if (notify && started) ShowToast("Back on normal Wi-Fi", IconCheck);
                }
            }
        }
        finally
        {
            _temporaryLinkClosing = false;
        }
    }

    private void StopTemporaryLinkResources()
    {
        _temporaryLinkClosing = true;
        try
        {
            StopLinkExpiryTimer();
            ReleaseScanner();
            var hotspot = _temporaryHotspot;
            _temporaryHotspot = null;
            var wifiLink = _temporaryWifiLink;
            _temporaryWifiLink = null;
            try
            {
                hotspot?.Stop();
                hotspot?.Dispose();
            }
            catch
            {
            }
            try
            {
                wifiLink?.Disconnect();
                wifiLink?.Dispose();
            }
            catch
            {
            }
            _temporaryLinkActive = false;
            _temporaryLinkRole = "";
            _activeLink = null;
            _linkPeer = null;
        }
        finally
        {
            _temporaryLinkClosing = false;
        }
    }

    private void ShowLinkOverlay()
    {
        LinkOverlay.IsVisible = true;
        LinkOverlay.Opacity = 1;
        UpdateLinkCard();
    }

    private void HideLinkOverlay()
    {
        ReleaseScanner();
        LinkOverlay.IsVisible = false;
        LinkOverlay.Opacity = 0;
        if (!_temporaryLinkActive) ShowLinkPanels();
    }

    private void ShowLinkPanels(bool host = false, bool scan = false,
        bool connecting = false, bool connected = false)
    {
        LinkHostPanel.IsVisible = host;
        LinkScanPanel.IsVisible = scan;
        LinkConnectingPanel.IsVisible = connecting;
        LinkConnectedPanel.IsVisible = connected;
        if (!scan) ReleaseScanner();
    }

    private void StartScannerDetection()
    {
        try
        {
            if (_linkScanner is null)
            {
                _linkScanner = new CameraBarcodeReaderView
                {
                    CameraLocation = CameraLocation.Rear,
                    IsDetecting = false
                };
                _linkScanner.BarcodesDetected += Scanner_BarcodesDetected;
                ScannerHost.Children.Add(_linkScanner);
            }
            _linkScanner.Options = new BarcodeReaderOptions
            {
                Formats = BarcodeFormat.QrCode,
                AutoRotate = true,
                Multiple = false,
                TryHarder = true,
                DelayBetweenAnalyzingFrames = 120,
                InitialDelayBeforeAnalyzingFrames = 200
            };
            _linkScanner.IsVisible = true;
            _linkScanner.IsDetecting = true;
            LinkScanSpinner.IsRunning = true;
            LinkScanStatusLabel.Text = "Point at the other phone's QR code";
        }
        catch (Exception exception)
        {
            LinkScanStatusLabel.Text = $"Camera unavailable: {exception.Message}";
            LinkScanSpinner.IsRunning = false;
            ReleaseScanner();
            ShowToast("The camera could not be started. Check Camera permission in Settings → Apps → Wisp.",
                IconError, error: true, durationSeconds: 7);
        }
    }

    private void StopScannerDetection()
    {
        try
        {
            if (_linkScanner is not null) _linkScanner.IsDetecting = false;
        }
        catch
        {
        }
        LinkScanSpinner.IsRunning = false;
    }

    private void ReleaseScanner()
    {
        StopScannerDetection();
        var scanner = _linkScanner;
        _linkScanner = null;
        if (scanner is null) return;
        try
        {
            scanner.BarcodesDetected -= Scanner_BarcodesDetected;
        }
        catch
        {
        }
        try
        {
            if (ScannerHost.Children.Contains(scanner)) ScannerHost.Children.Remove(scanner);
        }
        catch
        {
        }
    }

    private async Task<bool> EnsureLinkPermissionsAsync(bool needCamera)
    {
        try
        {
            if (needCamera)
            {
                var camera = await Permissions.CheckStatusAsync<Permissions.Camera>();
                if (camera != PermissionStatus.Granted)
                    camera = await Permissions.RequestAsync<Permissions.Camera>();
                if (camera != PermissionStatus.Granted)
                {
                    ShowToast("Camera access is needed to scan a temporary link code.",
                        IconError, error: true);
                    return false;
                }
            }

            var nearbyGranted = OperatingSystem.IsAndroidVersionAtLeast(33) &&
                ContextCompat.CheckSelfPermission(AndroidApplication.Context,
                    global::Android.Manifest.Permission.NearbyWifiDevices) == Permission.Granted;
            if (OperatingSystem.IsAndroidVersionAtLeast(33) && !nearbyGranted)
            {
                var nearbyStatus = await Permissions.RequestAsync<Permissions.NearbyWifiDevices>();
                nearbyGranted = nearbyStatus == PermissionStatus.Granted;
            }
            if (!nearbyGranted)
            {
                var location = await Permissions.CheckStatusAsync<Permissions.LocationWhenInUse>();
                if (location != PermissionStatus.Granted)
                    location = await Permissions.RequestAsync<Permissions.LocationWhenInUse>();
                if (location != PermissionStatus.Granted)
                {
                    ShowToast("Allow Nearby devices or Location so Wisp can create or join a temporary Wi-Fi link.",
                        IconError, error: true, durationSeconds: 7);
                    return false;
                }
            }
            AndroidPermissions.RequestAll();
            return true;
        }
        catch (Exception exception)
        {
            ShowToast($"Could not request permissions: {exception.Message}",
                IconError, error: true);
            return false;
        }
    }

    private void StartLinkExpiryTimer(QrLinkPayload payload)
    {
        StopLinkExpiryTimer();
        UpdateLinkExpiry(payload);
        _linkExpiryTimer = Dispatcher.CreateTimer();
        _linkExpiryTimer.Interval = TimeSpan.FromSeconds(1);
        _linkExpiryTimer.IsRepeating = true;
        _linkExpiryTimer.Tick += (_, _) =>
        {
            if (_temporaryLinkClosing || !_temporaryLinkActive || _temporaryLinkRole != "host")
            {
                StopLinkExpiryTimer();
                return;
            }
            if (_linkPeerJoined || HasActiveTransfers())
            {
                LinkExpiryLabel.Text = "Link is in use until you disconnect.";
                StopLinkExpiryTimer();
                return;
            }
            if (UpdateLinkExpiry(payload))
            {
                StopLinkExpiryTimer();
                ShowToast("The temporary connection code expired.", IconInfo);
                _ = DisconnectTemporaryLinkAsync(restartEngine: true, notify: false);
            }
        };
        _linkExpiryTimer.Start();
    }

    private bool UpdateLinkExpiry(QrLinkPayload payload)
    {
        var remaining = payload.ExpiresAt - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            LinkExpiryLabel.Text = "Code expired";
            return true;
        }
        LinkExpiryLabel.Text = remaining.TotalMinutes >= 1
            ? $"Code expires in {remaining.Minutes}:{remaining.Seconds:00}"
            : $"Code expires in {remaining.Seconds}s";
        return false;
    }

    private void StopLinkExpiryTimer()
    {
        _linkExpiryTimer?.Stop();
        _linkExpiryTimer = null;
    }

    private void UpdateLinkCard()
    {
        LinkActiveBadge.IsVisible = _temporaryLinkActive;
        LinkSummaryLabel.IsVisible = _temporaryLinkActive;
        LinkIdleActions.IsVisible = !_temporaryLinkActive;
        LinkActiveActions.IsVisible = _temporaryLinkActive;
        LinkCardHintLabel.Text = _temporaryLinkActive
            ? _temporaryLinkRole == "host"
                ? "Hosting a private Wi-Fi link. Open the link to show the QR code again."
                : "Connected over a private link. Open the link to send files or disconnect."
            : "No shared Wi-Fi? Make a private hotspot and scan a QR code.";
        if (!_temporaryLinkActive)
        {
            LinkSummaryLabel.Text = "";
            return;
        }
        if (_temporaryLinkRole == "host" && _activeLink is not null)
        {
            LinkSummaryLabel.Text = _linkPeerJoined
                ? $"Private link · a phone joined at {_activeLink.EndpointText}"
                : $"Hosting a private link · {_activeLink.EndpointText}";
            if (!_linkPeerJoined)
                LinkHostHintLabel.Text = "Waiting for the other phone to scan this code…";
        }
        else if (_temporaryLinkRole == "scan" && _activeLink is not null)
        {
            LinkSummaryLabel.Text = $"Private link · connected to {_activeLink.DeviceName}";
        }
    }

    private static async Task<(string Host, bool UsedFallback)> ResolveHotspotHostAsync()
    {
        // The soft-AP interface is not always configured the instant onStarted
        // fires, so give the kernel a few hundred milliseconds to assign IPv4.
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var address = HotspotAddressResolver.Resolve(AndroidApplication.Context);
            if (!string.IsNullOrWhiteSpace(address)) return (address, false);
            await Task.Delay(250);
        }
        return (HotspotAddressResolver.CommonFallbackAddress, true);
    }

    private static string NewLinkToken() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    private static string ShortFingerprint(string fingerprint) =>
        fingerprint.Length <= 20 ? fingerprint : $"{fingerprint[..10]}…{fingerprint[^10..]}";

    private static async Task WaitForWifiRestoreAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var manager = (ConnectivityManager)AndroidApplication.Context
                    .GetSystemService(Context.ConnectivityService)!;
                var network = manager.ActiveNetwork;
                if (network is not null)
                {
                    var capabilities = manager.GetNetworkCapabilities(network);
                    // The previous home Wi-Fi has internet capability; the
                    // just-released LocalOnlyHotspot does not.
                    if (capabilities?.HasTransport(TransportType.Wifi) == true &&
                        capabilities.HasCapability(NetCapability.Internet)) return;
                }
            }
            catch
            {
            }
            await Task.Delay(350);
        }
    }

    // ── Onboarding ────────────────────────────────────────────────────

    private void OnboardingPermissions_Click(object sender, EventArgs e)
    {
        AndroidPermissions.RequestAll(force: true);
        ShowToast("Review the permission prompts to keep Wisp working locally", IconInfo);
    }

    private async void FinishOnboarding_Click(object sender, EventArgs e)
    {
        _settings.Onboarded = true;
        _settingsStore.Save(_settings);
        await OnboardingOverlay.FadeTo(0, 160, Easing.CubicIn);
        OnboardingOverlay.IsVisible = false;
        if (!_started)
        {
            _started = true;
            await StartEngineAsync();
        }
        _ = ProcessSharedFilesAsync();
        await ProcessAppActionsAsync();
    }

    // ── Settings categories ───────────────────────────────────────────

    private void SettingsCategory_Tapped(object? sender, TappedEventArgs e)
    {
        if (e.Parameter is string category) SetSettingsCategory(category);
    }

    private void SetSettingsCategory(string category)
    {
        _settingsCategory = category;
        SettingsTitleBlock.IsVisible = false;
        SettingsMenuPanel.IsVisible = false;
        SettingsDetailHeader.IsVisible = true;

        var general = category == "general";
        var transfer = category == "transfer";
        var security = category == "security";
        var about = category == "about";

        SettingsCategoryTitle.Text = category switch
        {
            "general" => "Appearance & sounds",
            "transfer" => "Transfer & permissions",
            "security" => "Security & devices",
            _ => "About Wisp"
        };

        AppearanceCard.IsVisible = general;
        FeedbackCard.IsVisible = general;
        ThisDeviceCard.IsVisible = general;

        ReceivingCard.IsVisible = transfer;
        PermissionsCard.IsVisible = transfer;

        SecurityCard.IsVisible = security;
        IdentityCard.IsVisible = security;
        TrustedHeaderLabel.IsVisible = security;
        TrustedCountLabel.IsVisible = security;
        TrustedHost.IsVisible = security && Trusted.Count > 0;
        TrustedEmpty.IsVisible = security && Trusted.Count == 0;

        AboutCard.IsVisible = about;
    }

    private void ShowSettingsMenu()
    {
        SettingsTitleBlock.IsVisible = true;
        SettingsMenuPanel.IsVisible = true;
        SettingsDetailHeader.IsVisible = false;
        AppearanceCard.IsVisible = false;
        FeedbackCard.IsVisible = false;
        ThisDeviceCard.IsVisible = false;
        ReceivingCard.IsVisible = false;
        PermissionsCard.IsVisible = false;
        SecurityCard.IsVisible = false;
        IdentityCard.IsVisible = false;
        TrustedHeaderLabel.IsVisible = false;
        TrustedCountLabel.IsVisible = false;
        TrustedHost.IsVisible = false;
        TrustedEmpty.IsVisible = false;
        AboutCard.IsVisible = false;
    }

    private void SettingsBack_Click(object sender, EventArgs e) => ShowSettingsMenu();

    // ── Biometrics ────────────────────────────────────────────────────

    private void BiometricAcceptToggled(object? sender, ToggledEventArgs e)
    {
        if (e.Value && !AndroidBiometrics.IsAvailable)
        {
            BiometricAcceptSwitch.IsToggled = false;
            ShowToast("No biometrics or screen lock is set up on this device", IconError, error: true);
            return;
        }
        _settings.BiometricAccept = e.Value;
        _settingsStore.Save(_settings);
    }

    private void BiometricUnlockToggled(object? sender, ToggledEventArgs e)
    {
        if (e.Value && !AndroidBiometrics.IsAvailable)
        {
            BiometricUnlockSwitch.IsToggled = false;
            ShowToast("Set up a fingerprint or screen lock first", IconError, error: true);
            return;
        }
        _settings.BiometricUnlock = e.Value;
        _settingsStore.Save(_settings);
        if (e.Value) ShowToast("Wisp will ask for biometrics next time it opens", IconCheck);
    }

    private async Task TryUnlockAsync()
    {
        if (_unlockPromptActive) return;
        if (!_settings.BiometricUnlock)
        {
            LockOverlay.IsVisible = false;
            return;
        }
        if (!AndroidBiometrics.IsAvailable)
        {
            _settings.BiometricUnlock = false;
            BiometricUnlockSwitch.IsToggled = false;
            _settingsStore.Save(_settings);
            _unlocked = true;
            LockOverlay.IsVisible = false;
            ShowToast("Biometrics unavailable; app lock disabled", IconInfo);
            return;
        }
        _unlockPromptActive = true;
        var ok = await AndroidBiometrics.AuthenticateAsync("Unlock Wisp", "Confirm it's you");
        _unlockPromptActive = false;
        if (ok)
        {
            _unlocked = true;
            LockOverlay.IsVisible = false;
        }
        else
        {
            LockOverlay.IsVisible = true;
            ShowToast("Authentication required", IconError, error: true);
        }
    }

    private async void UnlockApp_Click(object sender, EventArgs e) => await TryUnlockAsync();

    private DateTime? _backgroundedAt;

    public void OnAppBackgrounded()
    {
        _appVisible = false;
        // The native viewer has no background media session; stop playback
        // rather than leaving a video or track running behind the launcher.
        MediaViewer.DismissCurrent();
        if (_settings.BiometricUnlock) _backgroundedAt = DateTime.UtcNow;
    }

    public void OnAppResumed()
    {
        _appVisible = true;
        if (!_settings.BiometricUnlock || _unlocked || _backgroundedAt is null) return;
        var elapsed = DateTime.UtcNow - _backgroundedAt.Value;
        _backgroundedAt = null;
        if (elapsed.TotalSeconds < 20) return;
        _unlocked = false;
        LockOverlay.IsVisible = true;
        _ = TryUnlockAsync();
    }

    // ── Toast ─────────────────────────────────────────────────────────

    private void ShowToast(string message, string icon = IconCheck, bool error = false, int durationSeconds = 4)
    {
        // Android system bottom toast (Material Snackbar); failures and long
        // messages stay visible longer.
        AndroidFeedback.Show(message, error, durationSeconds > 5);
    }

    // ── Android platform glue ─────────────────────────────────────────

    private void StartForegroundService(string text)
    {
        if (_foregroundActive) return;
        try
        {
            WispForegroundService.Start(AndroidApplication.Context, text);
            _foregroundActive = true;
        }
        catch
        {
        }
    }

    private void UpdateForegroundService()
    {
        if (Transfers.Any(t => t.IsActive)) return;
        // Keep the foreground service alive: it also keeps LAN discovery
        // running while the screen is off.
        UpdateWidget("idle", "Wisp", "Ready nearby", -1);
    }

    private void StopForegroundService()
    {
        if (!_foregroundActive) return;
        _foregroundActive = false;
        try
        {
            WispForegroundService.Stop(AndroidApplication.Context);
        }
        catch
        {
        }
    }

    private void ResetWidgetLater()
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(18));
            if (_closing || HasActiveTransfers()) return;
            MainThread.BeginInvokeOnMainThread(() =>
                UpdateWidget("idle", _settings.DeviceName, "Ready nearby", -1));
        });
    }

    private void UpdateWidget(string mode, string title, string status, int progress)
    {
        try
        {
            WispWidgetProvider.Update(AndroidApplication.Context, mode, title, status, progress);
        }
        catch
        {
        }
    }

    private bool BindToWifiNetwork()
    {
        try
        {
            var connectivity = (ConnectivityManager)AndroidApplication.Context
                .GetSystemService(Context.ConnectivityService)!;
            Network? fallback = null;
#pragma warning disable CA1422 // GetAllNetworks still works on current Android releases.
            foreach (var network in connectivity.GetAllNetworks())
            {
                var capabilities = connectivity.GetNetworkCapabilities(network);
                if (capabilities?.HasTransport(TransportType.Wifi) != true) continue;
                if (capabilities.HasCapability(NetCapability.Internet))
                {
                    if (connectivity.BindProcessToNetwork(network))
                    {
                        _boundWifiNetwork = network;
                        return true;
                    }
                }
                fallback ??= network;
            }
#pragma warning restore CA1422
            if (fallback is not null && connectivity.BindProcessToNetwork(fallback))
            {
                _boundWifiNetwork = fallback;
                return true;
            }
            // Do not leave the process pinned to a network that vanished while
            // the hotspot was active.
            connectivity.BindProcessToNetwork(null);
            _boundWifiNetwork = null;
        }
        catch
        {
        }
        return false;
    }

    private void UnbindWifiNetwork()
    {
        try
        {
            var connectivity = (ConnectivityManager)AndroidApplication.Context
                .GetSystemService(Context.ConnectivityService)!;
            connectivity.BindProcessToNetwork(null);
        }
        catch
        {
        }
        _boundWifiNetwork = null;
    }

    private void AcquireMulticastLock()
    {
        if (_multicastLock is not null) return;
        try
        {
            var wifi = (WifiManager)AndroidApplication.Context.GetSystemService(Context.WifiService)!;
            var multicastLock = wifi.CreateMulticastLock("wisp-nearby")!;
            multicastLock.SetReferenceCounted(false);
            multicastLock.Acquire();
            _multicastLock = multicastLock;
        }
        catch
        {
        }
    }

    private void ReleaseMulticastLock()
    {
        try
        {
            if (_multicastLock?.IsHeld == true) _multicastLock.Release();
        }
        catch
        {
        }
        _multicastLock = null;
    }

    private void RequestNotificationPermission()
    {
        try
        {
            if (Build.VERSION.SdkInt < BuildVersionCodes.Tiramisu) return;
            var activity = Platform.CurrentActivity;
            if (activity is null) return;
            if (ContextCompat.CheckSelfPermission(activity, global::Android.Manifest.Permission.PostNotifications)
                != Permission.Granted)
            {
                ActivityCompat.RequestPermissions(activity,
                    new[] { global::Android.Manifest.Permission.PostNotifications }, 7301);
            }
        }
        catch
        {
        }
    }
}
