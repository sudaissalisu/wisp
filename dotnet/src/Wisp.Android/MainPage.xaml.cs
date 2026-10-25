using System.Collections.ObjectModel;
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

    private readonly Dictionary<string, PeerItem> _peerById = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TransferItem> _transferById = new(StringComparer.OrdinalIgnoreCase);
    private readonly SettingsStore _settingsStore;
    private readonly TrustStore _trustStore;
    private readonly HistoryStore _historyStore;
    private readonly string _identityDirectory;
    private readonly ActivityGaugeDrawable _gaugeDrawable = new();
    private AppSettings _settings;
    private WispEngine? _engine;
    private WifiManager.MulticastLock? _multicastLock;
    private Network? _boundWifiNetwork;
    private IDispatcherTimer? _toastTimer;
    private IDispatcherTimer? _gaugeTimer;
    private CancellationTokenSource? _pulseCts;
    private TransferOffer? _pendingOffer;
    private bool _started;
    private bool _closing;
    private bool _foregroundActive;
    private bool _processingShares;
    private string _activeSection = "nearby";
    private string _settingsCategory = "general";
    private readonly List<string> _pendingSharePaths = [];
    private string? _activeTransferId;
    private bool _transferMinimized;
    private bool _unlocked;
    private bool _unlockPromptActive;
    private bool _appVisible;
    private int _lastPeerCount;
    private long _lastProgressNotificationMs;
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
        AboutLabel.Text = $"Wisp {WispEngine.Version} · protocol 1 · local only";
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
    public ObservableCollection<TrustedItem> Trusted { get; } = [];

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _appVisible = false;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        _appVisible = true;
        StartPresencePulse();
        AndroidTheme.Apply(Application.Current?.RequestedTheme == AppTheme.Light);
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
    }

    public void Shutdown()
    {
        if (_closing) return;
        _closing = true;
        ShareInbox.Changed -= OnShareInboxChanged;
        AppActionInbox.Changed -= OnAppActionChanged;
        StopPresencePulse();
        StopGaugeTimer();
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

    private async Task StartEngineAsync()
    {
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
        BindToWifiNetwork();
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
                DiscoveryPort = 47778,
                DataChannels = 4
            }));
            UpdateIdentityUi();
            SetHeaderStatus("visible", true);
            PresenceStatusLabel.Text = "Visible on this Wi-Fi";
            PresenceDot.Color = MintColor;
            StartForegroundService("Visible nearby · transfers keep running");
            ShowToast("You’re live", IconCheck);
            UpdateWidget("idle", _settings.DeviceName, "Ready nearby", -1);
            await ProcessAppActionsAsync();
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
        MainThread.BeginInvokeOnMainThread(async () => await HandleOfferAsync(offer));
    }

    private void OnPairing(object? sender, PairingInfo pairing)
    {
        if (!ReferenceEquals(sender, _engine)) return;
        MainThread.BeginInvokeOnMainThread(() =>
            ShowToast($"Verify code {pairing.Code} with {pairing.PeerName}", IconInfo, durationSeconds: 12));
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
            UpdateTransferUi();
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
            PresenceStatusLabel.Text = count == 0 ? "Visible on this Wi-Fi" : $"Visible · {count} nearby";
            PresenceSubLabel.Text = $"Visible on this Wi-Fi · port {self.Port}";
            PeerIdLabel.Text = self.Id;
            FingerprintLabel.Text = self.Fingerprint;
        }
        UpdateHeaderGauge();
        UpdateShareSheetState();
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
        PresenceSubLabel.Text = $"Visible on this Wi-Fi · port {self.Port}";
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

    // ── Offers ────────────────────────────────────────────────────────

    private async Task HandleOfferAsync(TransferOffer offer)
    {
        if (_engine is null || _closing) return;
        var trusted = _trustStore.Find(offer.Fingerprint);
        if (_settings.AutoAcceptTrusted && trusted?.AutoAccept == true)
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
        OfferSummaryLabel.Text = $"{offer.FileCountText} · {offer.SizeText}";
        var names = string.Join("\n", offer.Files.Take(4).Select(f => f.Path));
        if (offer.FileCount > 4) names += $"\nand {offer.FileCount - 4} more…";
        OfferFilesLabel.Text = names;
        OfferCodeLabel.Text = offer.PairingCode;
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
        var peer = _engine?.Peers.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
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
    private void NavSettings_Tapped(object? sender, TappedEventArgs e) => SetSection("settings");
    private void HeaderActivity_Tapped(object? sender, TappedEventArgs e) => SetSection("transfers");

    private void SetSectionInitial()
    {
        _activeSection = "nearby";
        NearbyScroll.IsVisible = true;
        TransfersScroll.IsVisible = false;
        SettingsScroll.IsVisible = false;
        StyleNav(NavNearbyPill, NavNearbyIcon, NavNearbyText, true);
        StyleNav(NavTransfersPill, NavTransfersIcon, NavTransfersText, false);
        StyleNav(NavSettingsPill, NavSettingsIcon, NavSettingsText, false);
    }

    private void SetSection(string section)
    {
        if (_activeSection == section) return;
        _activeSection = section;
        var incoming = section == "nearby" ? (VisualElement)NearbyScroll
            : section == "transfers" ? (VisualElement)TransfersScroll
            : (VisualElement)SettingsScroll;
        NearbyScroll.IsVisible = section == "nearby";
        TransfersScroll.IsVisible = section == "transfers";
        SettingsScroll.IsVisible = section == "settings";
        incoming.Opacity = 0;
        incoming.TranslationY = 14;
        _ = incoming.FadeTo(1, 200, Easing.CubicOut);
        _ = incoming.TranslateTo(0, 0, 220, Easing.CubicOut);
        StyleNav(NavNearbyPill, NavNearbyIcon, NavNearbyText, section == "nearby");
        StyleNav(NavTransfersPill, NavTransfersIcon, NavTransfersText, section == "transfers");
        StyleNav(NavSettingsPill, NavSettingsIcon, NavSettingsText, section == "settings");
        if (section == "nearby") _ = ProcessSharedFilesAsync();
        if (section == "transfers") RefreshHistory();
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
        _settings.DeviceName = name;
        _settings.AutoAcceptTrusted = AutoAcceptSwitch.IsToggled;
        _settings.SoundsEnabled = SoundsSwitch.IsToggled;
        _settings.NotificationsEnabled = NotificationsSwitch.IsToggled;
        _settingsStore.Save(_settings);
        SettingsStatusLabel.Text = "Restarting…";
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
        SharePeersHost.IsVisible = Peers.Count > 0;
        ShareEmptyLabel.IsVisible = Peers.Count == 0;
        ShareScanSpinner.IsRunning = Peers.Count == 0;
    }

    private void UpdateShareSheetState()
    {
        try
        {
            var hasPeers = Peers.Count > 0;
            SharePeersHost.IsVisible = hasPeers;
            ShareEmptyLabel.IsVisible = !hasPeers;
            ShareScanSpinner.IsRunning = !hasPeers && ShareSheetOverlay.IsVisible;
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

    private async void SharePeer_Tapped(object? sender, TappedEventArgs e)
    {
        if (e.Parameter is not string id) return;
        var peer = _engine?.Peers.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (peer is null || _pendingSharePaths.Count == 0) return;
        var paths = _pendingSharePaths.ToArray();
        _pendingSharePaths.Clear();
        await ShareSheetOverlay.FadeTo(0, 120, Easing.CubicIn);
        ShareSheetOverlay.IsVisible = false;
        ShareScanSpinner.IsRunning = false;
        await SendToPeerAsync(peer, paths);
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
        TransferPeerLabel.Text = incoming ? $"Receiving from {peerName}" : $"Sending to {peerName}";
        TransferGlyph.Text = incoming ? "" : "";
        TransferStateLabel.Text = state;
        TransferProgressBar.Progress = 0;
        TransferPercentLabel.Text = "0%";
        TransferSpeedLabel.Text = "";
        MiniProgressBar.Progress = 0;
        MiniProgressLabel.Text = "0%";
        MiniProgressGlyph.Text = incoming ? "" : "";
        _transferMinimized = false;
        TransferOverlay.IsVisible = true;
        TransferOverlay.Opacity = 0;
        _ = TransferOverlay.FadeTo(1, 160, Easing.CubicOut);
        MiniProgressPill.IsVisible = false;
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
        MiniProgressBar.Progress = pct;
        MiniProgressLabel.Text = $"{pct * 100:0}%";
        MiniProgressGlyph.Text = progress.IsIncoming ? "" : "";
        TransferActionButton.Text = "Cancel";
        if (!_transferMinimized) TransferOverlay.IsVisible = true;
        else MiniProgressPill.IsVisible = true;
    }

    private void FinishTransferOverlay(string transferId, bool success)
    {
        if (!string.IsNullOrEmpty(_activeTransferId) &&
            !_activeTransferId.Equals(transferId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        TransferProgressBar.Progress = 1;
        TransferPercentLabel.Text = "100%";
        TransferSpeedLabel.Text = "";
        TransferStateLabel.Text = success ? "Complete" : "Stopped";
        TransferActionButton.Text = "Done";
        MiniProgressBar.Progress = 1;
        MiniProgressLabel.Text = success ? "100%" : "!";
        MiniProgressGlyph.Text = success ? "" : "";
        _activeTransferId = null;
        if (!_transferMinimized)
        {
            TransferOverlay.IsVisible = true;
        }
        else
        {
            MiniProgressPill.IsVisible = true;
        }
    }

    private void MinimizeTransfer_Click(object sender, EventArgs e)
    {
        _transferMinimized = true;
        TransferOverlay.IsVisible = false;
        MiniProgressPill.IsVisible = true;
    }

    private void RestoreTransferOverlay_Tapped(object? sender, TappedEventArgs e)
    {
        _transferMinimized = false;
        MiniProgressPill.IsVisible = false;
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
        MiniProgressPill.IsVisible = false;
        _transferMinimized = false;
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

    private void BindToWifiNetwork()
    {
        try
        {
            var connectivity = (ConnectivityManager)AndroidApplication.Context
                .GetSystemService(Context.ConnectivityService)!;
            foreach (var network in connectivity.GetAllNetworks())
            {
                var capabilities = connectivity.GetNetworkCapabilities(network);
                if (capabilities?.HasTransport(TransportType.Wifi) != true) continue;
                if (connectivity.BindProcessToNetwork(network)) _boundWifiNetwork = network;
                break;
            }
        }
        catch
        {
        }
    }

    private void UnbindWifiNetwork()
    {
        try
        {
            if (_boundWifiNetwork is null) return;
            var connectivity = (ConnectivityManager)AndroidApplication.Context
                .GetSystemService(Context.ConnectivityService)!;
            connectivity.BindProcessToNetwork(null);
            _boundWifiNetwork = null;
        }
        catch
        {
        }
    }

    private void AcquireMulticastLock()
    {
        try
        {
            var wifi = (WifiManager)AndroidApplication.Context.GetSystemService(Context.WifiService)!;
            _multicastLock = wifi.CreateMulticastLock("wisp-nearby");
            _multicastLock.SetReferenceCounted(false);
            _multicastLock.Acquire();
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
