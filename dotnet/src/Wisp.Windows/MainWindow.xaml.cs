using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI;
using Windows.UI.ViewManagement;
using Wisp.Core;

namespace Wisp.Windows;

public sealed partial class MainWindow : Window
{
    private readonly ObservableCollection<PeerItem> _peers = [];
    private readonly ObservableCollection<TransferItem> _transfers = [];
    private readonly ObservableCollection<TrustedDeviceItem> _trusted = [];
    private readonly Dictionary<string, PeerItem> _peerById = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TransferItem> _transferById = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _dialogGate = new(1, 1);
    private readonly DispatcherQueue _dispatcher;
    private readonly SettingsStore _settingsStore;
    private readonly TrustStore _trustStore;
    private readonly HistoryStore _historyStore;
    private readonly SoundService _sounds;
    private readonly string _identityDirectory;
    private readonly string _appDataDirectory;
    private AppSettings _settings;
    private WispEngine? _engine;
    private DispatcherQueueTimer? _toastTimer;
    private bool _closing;
    private bool _loaded;
    private bool _restarting;

    public MainWindow()
    {
        InitializeComponent();
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        _appDataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wisp");
        var appData = _appDataDirectory;
        Directory.CreateDirectory(appData);
        _identityDirectory = Path.Combine(appData, "identity");
        _settingsStore = new SettingsStore(Path.Combine(appData, "settings.json"));
        _trustStore = new TrustStore(Path.Combine(appData, "trusted.json"));
        _historyStore = new HistoryStore(Path.Combine(appData, "history.json"));
        _sounds = new SoundService(Path.Combine(AppContext.BaseDirectory, "Assets", "Sounds"));
        _settings = _settingsStore.Load();
        if (string.IsNullOrWhiteSpace(_settings.DeviceName))
            _settings.DeviceName = $"{Environment.UserName}'s PC";
        if (string.IsNullOrWhiteSpace(_settings.SaveDirectory))
            _settings.SaveDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Wisp");
        Directory.CreateDirectory(_settings.SaveDirectory);

        PeerRepeater.ItemsSource = _peers;
        TransferRepeater.ItemsSource = _transfers;
        TrustedRepeater.ItemsSource = _trusted;

        DeviceNameBox.Text = _settings.DeviceName;
        SaveDirectoryBox.Text = _settings.SaveDirectory;
        AutoAcceptTrustedToggle.IsOn = _settings.AutoAcceptTrusted;
        SoundsToggle.IsOn = _settings.SoundsEnabled;
        _sounds.Enabled = _settings.SoundsEnabled;
        AboutText.Text = $"Wisp {typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "0.2.0"}";
        RefreshTrusted();
        RefreshHistory();
        SetupTrayIcon();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarDragRegion);
        AppWindow.Resize(new SizeInt32(1240, 800));
        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
            if (File.Exists(iconPath)) AppWindow.SetIcon(iconPath);
        }
        catch
        {
        }
        try
        {
            SystemBackdrop = new MicaBackdrop();
        }
        catch
        {
            // Mica is unavailable on some older builds; the solid theme background
            // remains readable.
        }

        NavView.SelectedItem = NearbyNavItem;
        ShowSection("nearby");
        UpdateEmptyStates();
        UpdateStatusUi();

        RootGrid.Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
    }


    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        StartPulseAnimation();
        if (!_settings.Onboarded)
        {
            await ShowOnboardingAsync();
            _settings.Onboarded = true;
            _settingsStore.Save(_settings);
        }
        await StartEngineAsync();
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _closing = true;
        var engine = _engine;
        _engine = null;
        if (engine is not null) _ = Task.Run(engine.Dispose);
    }

    private async Task ShowOnboardingAsync()
    {
        try
        {
            var content = new StackPanel { Spacing = 16, Width = 500 };
            var heading = new StackPanel { Spacing = 3, HorizontalAlignment = HorizontalAlignment.Center };
            heading.Children.Add(new Border
            {
                Width = 64,
                Height = 64,
                CornerRadius = new CornerRadius(22),
                Background = new LinearGradientBrush
                {
                    StartPoint = new global::Windows.Foundation.Point(0, 0),
                    EndPoint = new global::Windows.Foundation.Point(1, 1),
                    GradientStops =
                    {
                        new GradientStop { Color = Color.FromArgb(255, 91, 141, 239), Offset = 0 },
                        new GradientStop { Color = Color.FromArgb(255, 69, 227, 180), Offset = 1 }
                    }
                },
                HorizontalAlignment = HorizontalAlignment.Center,
                Child = new TextBlock
                {
                    Text = "w",
                    FontSize = 28,
                    FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                    Foreground = new SolidColorBrush(Color.FromArgb(255, 8, 18, 15)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            });
            heading.Children.Add(new TextBlock
            {
                Text = "Welcome to Wisp",
                FontFamily = new FontFamily("Segoe UI Variable Display"),
                FontSize = 28,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center
            });
            heading.Children.Add(new TextBlock
            {
                Text = "Files, directly between nearby devices. No account, no cloud.",
                FontSize = 13,
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            });
            content.Children.Add(heading);

            foreach (var (glyph, title, body) in new[]
                     {
                         ("", "See nearby devices", "Keep Wisp open on both devices; nearby Windows PCs and Android phones appear automatically."),
                         ("", "Private by design", "TLS 1.3, a six-digit pairing code, and a pinned device key. Nothing leaves your Wi-Fi."),
                         ("", "Live, fast transfers", "Drop files on a device, follow speed and ETA, and keep a history of everything that landed.")
                     })
            {
                var row = new Grid { ColumnSpacing = 12 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.Children.Add(new Border
                {
                    Width = 38,
                    Height = 38,
                    CornerRadius = new CornerRadius(13),
                    Background = new SolidColorBrush(Color.FromArgb(31, 69, 227, 180)),
                    Child = new FontIcon
                    {
                        Glyph = glyph,
                        FontFamily = new FontFamily("Segoe Fluent Icons"),
                        FontSize = 16,
                        Foreground = new SolidColorBrush(Color.FromArgb(255, 69, 227, 180))
                    }
                });
                var text = new StackPanel { Spacing = 1 };
                text.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
                text.Children.Add(new TextBlock { Text = body, FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
                Grid.SetColumn(text, 1);
                row.Children.Add(text);
                content.Children.Add(row);
            }

            content.Children.Add(new TextBlock
            {
                Text = "On first contact, compare the six-digit code on both screens. You can trust a device after accepting it.",
                FontSize = 11,
                Opacity = 0.55,
                TextWrapping = TextWrapping.Wrap
            });

            var dialog = new ContentDialog
            {
                Title = "Welcome",
                Content = content,
                PrimaryButtonText = "Get started",
                CloseButtonText = "Not now",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = RootGrid.XamlRoot
            };
            await dialog.ShowAsync();
        }
        catch
        {
        }
    }

    private async Task StartEngineAsync()
    {
        if (_closing) return;
        _restarting = true;
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
            _peers.Clear();
        }
        UpdateEmptyStates();
        HeaderStatusText.Text = "starting…";
        SelfFooterStatus.Text = "Starting…";
        PresenceStatusText.Text = "Connecting…";
        PresenceDot.Fill = new SolidColorBrush(Color.FromArgb(255, 120, 130, 127));
        HeaderStatusDot.Fill = new SolidColorBrush(Color.FromArgb(255, 120, 130, 127));

        var engine = new WispEngine();
        _engine = engine;
        Subscribe(engine);
        try
        {
            Directory.CreateDirectory(_settings.SaveDirectory);
            await Task.Run(() => engine.Start(new WispConfig
            {
                Name = _settings.DeviceName,
                SaveDirectory = _settings.SaveDirectory,
                IdentityDirectory = _identityDirectory,
                DiscoveryPort = 47778,
                DataChannels = 4
            }));
            UpdateIdentityUi();
            var peers = engine.Self;
            HeaderStatusText.Text = peers is null ? "offline" : "visible nearby";
            SelfFooterStatus.Text = "Visible on Wi-Fi";
            PresenceStatusText.Text = "Visible on this Wi-Fi";
            PresenceDot.Fill = new SolidColorBrush(Color.FromArgb(255, 69, 227, 180));
            HeaderStatusDot.Fill = new SolidColorBrush(Color.FromArgb(255, 69, 227, 180));
            NetworkDetailText.Text = peers is null ? "" :
                $"Peer port {peers.Port}  ·  discovery UDP 47778  ·  TLS 1.3";
            ShowToast("You’re live", "Keep Wisp open on both devices. Nearby devices will appear here.", InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            _engine = null;
            Unsubscribe(engine);
            engine.Dispose();
            HeaderStatusText.Text = "offline";
            SelfFooterStatus.Text = "Engine offline";
            PresenceStatusText.Text = "Could not start";
            ShowToast("Wisp could not start", exception.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _restarting = false;
        }
    }

    private void Subscribe(WispEngine engine)
    {
        engine.PeerChanged += OnPeerChanged;
        engine.OfferReceived += OnOfferReceived;
        engine.PairingCodeReceived += OnPairingCode;
        engine.ProgressChanged += OnProgressChanged;
        engine.TransferCompleted += OnTransferCompleted;
        engine.TransferFailed += OnTransferFailed;
        engine.TransferCancelled += OnTransferCancelled;
        engine.LogReceived += OnLogReceived;
    }

    private void Unsubscribe(WispEngine engine)
    {
        engine.PeerChanged -= OnPeerChanged;
        engine.OfferReceived -= OnOfferReceived;
        engine.PairingCodeReceived -= OnPairingCode;
        engine.ProgressChanged -= OnProgressChanged;
        engine.TransferCompleted -= OnTransferCompleted;
        engine.TransferFailed -= OnTransferFailed;
        engine.TransferCancelled -= OnTransferCancelled;
        engine.LogReceived -= OnLogReceived;
    }

    private void OnPeerChanged(object? sender, PeerEventArgs e)
    {
        if (!ReferenceEquals(sender, _engine)) return;
        _dispatcher.TryEnqueue(() => ApplyPeer(e));
    }

    private void OnOfferReceived(object? sender, TransferOffer offer)
    {
        if (!ReferenceEquals(sender, _engine)) return;
        _dispatcher.TryEnqueue(async () => await HandleOfferAsync(offer));
    }

    private void OnPairingCode(object? sender, PairingInfo pairing)
    {
        if (!ReferenceEquals(sender, _engine)) return;
        _dispatcher.TryEnqueue(() =>
        {
            // A pinned device already has an authenticated key; asking for the
            // short code again on every transfer would be redundant.
            if (IsTrustedPeer(pairing.PeerId)) return;
            ShowToast($"Verify code {pairing.Code}",
                $"Compare this code with {pairing.PeerName} before accepting. Matching codes prove the connection is direct.",
                InfoBarSeverity.Informational, autoClose: false);
        });
    }

    private bool IsTrustedPeer(string peerId)
    {
        if (string.IsNullOrWhiteSpace(peerId)) return false;
        var fingerprint = _engine?.Peers
            .FirstOrDefault(peer => peer.Id.Equals(peerId, StringComparison.OrdinalIgnoreCase))
            ?.Fingerprint;
        return !string.IsNullOrWhiteSpace(fingerprint) &&
               _trustStore.Find(fingerprint) is not null;
    }

    private void OnProgressChanged(object? sender, TransferProgress progress)
    {
        if (!ReferenceEquals(sender, _engine)) return;
        _dispatcher.TryEnqueue(() =>
        {
            var item = GetOrCreateTransfer(progress.TransferId, progress.PeerName, progress.Direction);
            item.ApplyProgress(progress);
            UpdateEmptyStates();
            UpdateHeaderGauge();
        });
    }

    private void OnTransferCompleted(object? sender, TransferCompletion completion)
    {
        if (!ReferenceEquals(sender, _engine)) return;
        _dispatcher.TryEnqueue(() =>
        {
            var item = GetOrCreateTransfer(completion.TransferId, completion.PeerName, completion.Direction);
            item.MarkComplete(completion);
            UpdateEmptyStates();
            _historyStore.Add(new HistoryEntry
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
            if (_settings.SoundsEnabled) _sounds.Play("success");
            if (completion.IsIncoming)
            {
                ShowToast("Delivered", $"From {completion.PeerName} · {TransferOffer.FormatBytes(completion.BytesTotal)}",
                    InfoBarSeverity.Success);
            }
            else
            {
                ShowToast("Sent", $"To {completion.PeerName} · {TransferOffer.FormatBytes(completion.BytesTotal)}",
                    InfoBarSeverity.Success);
            }
        });
    }

    private void OnTransferFailed(object? sender, TransferFailure failure)
    {
        if (!ReferenceEquals(sender, _engine)) return;
        _dispatcher.TryEnqueue(() =>
        {
            var item = GetOrCreateTransfer(failure.TransferId, failure.PeerName, failure.Direction);
            item.MarkFailed(failure);
            UpdateEmptyStates();
            _historyStore.Add(new HistoryEntry
            {
                Id = failure.TransferId,
                Direction = failure.Direction,
                PeerName = failure.PeerName,
                PeerId = failure.PeerId,
                State = "failed",
                Message = failure.Message
            });
            if (_settings.SoundsEnabled) _sounds.Play("failure");
            ShowToast("Transfer failed", failure.Message, InfoBarSeverity.Error);
        });
    }

    private void OnTransferCancelled(object? sender, TransferFailure failure)
    {
        if (!ReferenceEquals(sender, _engine)) return;
        _dispatcher.TryEnqueue(() =>
        {
            var item = GetOrCreateTransfer(failure.TransferId, failure.PeerName, failure.Direction);
            item.MarkFailed(failure with { Message = "Cancelled" });
            UpdateEmptyStates();
        });
    }

    private void OnLogReceived(object? sender, LogEventArgs e)
    {
        Debug.WriteLine($"[wisp:{e.Level}] {e.Message}");
    }

    private void ApplyPeer(PeerEventArgs args)
    {
        var before = _peers.Count;
        if (args.Online)
        {
            if (_peerById.TryGetValue(args.Peer.Id, out var item))
            {
                item.Update(args.Peer);
            }
            else
            {
                item = new PeerItem(args.Peer);
                _peerById[args.Peer.Id] = item;
                _peers.Add(item);
            }
        }
        else if (_peerById.Remove(args.Peer.Id, out var removed))
        {
            _peers.Remove(removed);
        }
        if (_settings.SoundsEnabled)
        {
            if (args.Online && before == 0 && _peers.Count > 0) _sounds.Play("connect");
            else if (!args.Online && _peers.Count == 0) _sounds.Play("disconnect");
        }
        UpdatePeerUi();
    }

    private void ApplyPeerSnapshot(IEnumerable<PeerInfo> peers)
    {
        var enumerated = peers.Where(p => p.Online).ToList();
        var online = enumerated.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var stale in _peerById.Keys.Where(id => !online.Contains(id)).ToList())
        {
            if (_peerById.Remove(stale, out var item)) _peers.Remove(item);
        }
        foreach (var peer in enumerated)
        {
            if (_peerById.TryGetValue(peer.Id, out var item)) item.Update(peer);
            else
            {
                item = new PeerItem(peer);
                _peerById[peer.Id] = item;
                _peers.Add(item);
            }
        }
        UpdatePeerUi();
    }

    private void UpdatePeerUi()
    {
        PeerCountText.Text = _peers.Count switch
        {
            0 => "Looking around…",
            1 => "1 device nearby",
            _ => $"{_peers.Count} devices nearby"
        };
        UpdateHeaderGauge();
        if (_engine?.Self is not null)
        {
            HeaderStatusText.Text = _peers.Count == 0 ? "visible nearby" : $"{_peers.Count} nearby";
            PresenceStatusText.Text = _peers.Count == 0
                ? "Visible on this Wi-Fi"
                : $"Visible · {_peers.Count} nearby";
        }
        UpdateEmptyStates();
    }

    private void UpdateIdentityUi()
    {
        var self = _engine?.Self;
        if (self is null) return;
        var initials = InitialsOf(self.Name);
        SelfInitialsText.Text = initials;
        SelfFooterInitials.Text = initials;
        IdentityInitials.Text = initials;
        SelfNameText.Text = self.Name;
        IdentityNameText.Text = self.Name;
        SelfFooterName.Text = self.Name;
        IdentityPeerIdText.Text = self.Id;
        IdentityFingerprintText.Text = self.Fingerprint;
        SelfSubtitleText.Text = "Visible to nearby devices on this Wi-Fi.";
        NetworkDetailText.Text = $"Peer port {self.Port}  ·  discovery UDP 47778  ·  TLS 1.3";
    }

    private void UpdateStatusUi()
    {
        if (_engine?.Self is null)
        {
            HeaderStatusText.Text = "offline";
            SelfFooterStatus.Text = "Engine offline";
        }
    }

    private void UpdateEmptyStates()
    {
        var hasPeers = _peers.Count > 0;
        PeerRepeater.Visibility = hasPeers ? Visibility.Visible : Visibility.Collapsed;
        NoPeersCard.Visibility = hasPeers ? Visibility.Collapsed : Visibility.Visible;
        var hasTransfers = _transfers.Count > 0;
        TransferRepeater.Visibility = hasTransfers ? Visibility.Visible : Visibility.Collapsed;
        NoTransfersCard.Visibility = hasTransfers ? Visibility.Collapsed : Visibility.Visible;
        UpdateHeaderGauge();
    }

    private TransferItem GetOrCreateTransfer(string transferId, string peerName, string direction)
    {
        if (_transferById.TryGetValue(transferId, out var existing)) return existing;
        var item = new TransferItem(transferId, string.IsNullOrWhiteSpace(peerName) ? "Nearby device" : peerName, direction);
        _transferById[transferId] = item;
        _transfers.Insert(0, item);
        return item;
    }

    private async Task HandleOfferAsync(TransferOffer offer)
    {
        if (_engine is null) return;
        try
        {
            // The offer fingerprint comes from the TLS certificate of this
            // connection, so a match means the same key the user pinned.
            var trustedPeer = _trustStore.Find(offer.Fingerprint);
            var trusted = trustedPeer is not null;
            if (_settings.AutoAcceptTrusted && trustedPeer?.AutoAccept == true)
            {
                AcceptOffer(offer, trust: false);
                ShowToast("Receiving", $"From {offer.PeerName} · {offer.FileCountText}", InfoBarSeverity.Success);
                return;
            }

            await _dialogGate.WaitAsync();
            try
            {
                if (_closing) return;
                TryBringToFront();
                var dialog = BuildOfferDialog(offer, trusted);
                var result = await dialog.Dialog.ShowAsync();
                if (result == ContentDialogResult.Primary)
                {
                    AcceptOffer(offer, trust: dialog.TrustCheckBox.IsChecked == true);
                }
                else
                {
                    _engine?.Respond(offer.OfferId, false);
                }
            }
            finally
            {
                _dialogGate.Release();
            }
        }
        catch (Exception exception)
        {
            ShowToast("Could not show the incoming offer", exception.Message, InfoBarSeverity.Error);
            _engine?.Respond(offer.OfferId, false);
        }
    }

    private void AcceptOffer(TransferOffer offer, bool trust)
    {
        if (_engine is null) return;
        if (trust && !string.IsNullOrWhiteSpace(offer.Fingerprint))
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
            BytesDone = 0,
            BytesTotal = offer.TotalBytes,
            FilesDone = 0,
            FilesTotal = offer.FileCount
        });
        UpdateEmptyStates();
        if (!_engine.Respond(offer.OfferId, true, _settings.SaveDirectory))
        {
            item.MarkFailed(new TransferFailure { Message = "The sender is no longer waiting.", TransferId = offer.TransferId });
            ShowToast("Offer expired", "The sender is no longer connected.", InfoBarSeverity.Warning);
        }
    }

    private OfferDialog BuildOfferDialog(TransferOffer offer, bool trusted)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = trusted ? "Files from a trusted device" : "Incoming files",
            PrimaryButtonText = "Accept",
            CloseButtonText = "Decline",
            DefaultButton = ContentDialogButton.Primary
        };

        var root = new StackPanel { Spacing = 14, Width = 470 };
        var heading = new Grid();
        var avatar = new Border
        {
            Width = 52,
            Height = 52,
            CornerRadius = new CornerRadius(17),
            Background = PeerPalette.BrushFor(offer.PeerId),
            Child = new TextBlock
            {
                Text = InitialsOf(offer.PeerName),
                FontSize = 15,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 8, 18, 15)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        var titleStack = new StackPanel { Margin = new Thickness(14, 0, 0, 0), Spacing = 2 };
        titleStack.Children.Add(new TextBlock
        {
            Text = offer.PeerName,
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        titleStack.Children.Add(new TextBlock
        {
            Text = $"{offer.FileCountText}  ·  {offer.SizeText}",
            FontSize = 12,
            Opacity = 0.65
        });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        heading.Children.Add(avatar);
        Grid.SetColumn(titleStack, 1);
        heading.Children.Add(titleStack);
        root.Children.Add(heading);

        var files = string.Join("\n", offer.Files.Take(4).Select(f => f.Path));
        if (offer.FilesTruncated > 0 || offer.FileCount > 4)
            files += $"\nand {Math.Max(offer.FilesTruncated, offer.FileCount - 4)} more…";
        root.Children.Add(new TextBlock
        {
            Text = files,
            FontSize = 12,
            Opacity = 0.72,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 86
        });

        if (trusted)
        {
            var trustedBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(20, 69, 227, 180)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(90, 69, 227, 180)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(14, 10, 14, 10)
            };
            var trustedStack = new StackPanel { Spacing = 2 };
            trustedStack.Children.Add(new TextBlock
            {
                Text = "TRUSTED DEVICE",
                FontSize = 9,
                CharacterSpacing = 80,
                Opacity = 0.65
            });
            trustedStack.Children.Add(new TextBlock
            {
                Text = "This device's key is already pinned, so there is no code to compare.",
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap
            });
            trustedBorder.Child = trustedStack;
            root.Children.Add(trustedBorder);
        }
        else
        {
            var codeBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(20, 69, 227, 180)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(90, 69, 227, 180)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(14, 10, 14, 10)
            };
            var codeStack = new StackPanel { Spacing = 2 };
            codeStack.Children.Add(new TextBlock { Text = "VERIFY THIS CODE ON THE OTHER DEVICE", FontSize = 9, CharacterSpacing = 80, Opacity = 0.65 });
            codeStack.Children.Add(new TextBlock
            {
                Text = offer.PairingCode,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 24,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                CharacterSpacing = 140
            });
            codeBorder.Child = codeStack;
            root.Children.Add(codeBorder);
        }

        var trust = new CheckBox { Content = "Trust this device and remember its key", IsChecked = false };
        if (!trusted) root.Children.Add(trust);

        dialog.Content = root;
        return new OfferDialog(dialog, trust);
    }

    private async Task SendFilesPickerAsync(PeerInfo? target)
    {
        try
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.Downloads,
                ViewMode = PickerViewMode.List
            };
            picker.FileTypeFilter.Add("*");
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            var files = await picker.PickMultipleFilesAsync();
            if (files is null || files.Count == 0) return;
            var paths = files.Select(f => f.Path).Where(File.Exists).ToArray();
            if (paths.Length == 0) return;
            if (target is null) await SendToBestPeerAsync(paths);
            else await SendToPeerAsync(target, paths);
        }
        catch (Exception exception)
        {
            ShowToast("Could not open the file picker", exception.Message, InfoBarSeverity.Error);
        }
    }

    private async Task SendFolderPickerAsync()
    {
        try
        {
            var picker = new FolderPicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary
            };
            picker.FileTypeFilter.Add("*");
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null || string.IsNullOrEmpty(folder.Path)) return;
            await SendToBestPeerAsync([folder.Path]);
        }
        catch (Exception exception)
        {
            ShowToast("Could not open the folder picker", exception.Message, InfoBarSeverity.Error);
        }
    }

    private async Task SendToBestPeerAsync(IReadOnlyList<string> paths)
    {
        if (_engine is null)
        {
            ShowToast("Wisp is offline", "Start the engine from Settings before sending.", InfoBarSeverity.Warning);
            return;
        }
        var peers = _engine.Peers.Where(p => p.Online).ToList();
        if (peers.Count == 0)
        {
            ShowToast("No devices nearby", "Keep Wisp open on the other device, or use Connect by address in Devices.", InfoBarSeverity.Warning);
            return;
        }
        if (peers.Count == 1)
        {
            await SendToPeerAsync(peers[0], paths);
            return;
        }

        await _dialogGate.WaitAsync();
        try
        {
            var list = new ListView
            {
                ItemsSource = peers,
                DisplayMemberPath = nameof(PeerInfo.Name),
                SelectedItem = peers[0],
                MaxHeight = 220,
                MinWidth = 340
            };
            var dialog = new ContentDialog
            {
                Title = "Send to which device?",
                Content = list,
                PrimaryButtonText = "Send",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = RootGrid.XamlRoot
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary &&
                list.SelectedItem is PeerInfo peer)
            {
                await SendToPeerAsync(peer, paths);
            }
        }
        finally
        {
            _dialogGate.Release();
        }
    }

    private async Task SendToPeerAsync(PeerInfo peer, IReadOnlyList<string> paths)
    {
        if (_engine is null) return;
        try
        {
            ShowToast("Sending…", $"Preparing {paths.Count} item(s) for {peer.Name}.", InfoBarSeverity.Informational);
            var id = await Task.Run(() => _engine.Send(paths, peer.Id));
            if (_transferById.TryGetValue(id, out var existing))
            {
                if (!existing.IsActive)
                    existing.ApplyProgress(new TransferProgress { TransferId = id, Direction = "send", State = "preparing", PeerId = peer.Id, PeerName = peer.Name });
            }
            else
            {
                var item = GetOrCreateTransfer(id, peer.Name, "send");
                item.ApplyProgress(new TransferProgress
                {
                    TransferId = id,
                    Direction = "send",
                    PeerId = peer.Id,
                    PeerName = peer.Name,
                    State = "preparing"
                });
            }
            UpdateEmptyStates();
        }
        catch (Exception exception)
        {
            ShowToast("Could not send", exception.Message, InfoBarSeverity.Error);
        }
    }

    private async void PeerCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string id) return;
        var peer = _engine?.Peers.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (peer is null) return;
        await SendFilesPickerAsync(peer);
    }

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Send with Wisp";
            e.DragUIOverride.IsCaptionVisible = true;
            DropOverlay.Visibility = Visibility.Visible;
        }
    }

    private void Root_DragLeave(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
    }

    private async void Root_Drop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        var paths = await ExtractDropPathsAsync(e);
        if (paths.Count > 0) await SendToBestPeerAsync(paths);
    }

    private void PeerCard_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Send to this device";
            e.DragUIOverride.IsCaptionVisible = true;
            DropOverlay.Visibility = Visibility.Visible;
        }
    }

    private async void PeerCard_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        DropOverlay.Visibility = Visibility.Collapsed;
        if (sender is not Button button || button.Tag is not string id) return;
        var peer = _engine?.Peers.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        var paths = await ExtractDropPathsAsync(e);
        if (peer is not null && paths.Count > 0) await SendToPeerAsync(peer, paths);
    }

    private static async Task<List<string>> ExtractDropPathsAsync(DragEventArgs e)
    {
        var paths = new List<string>();
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return paths;
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            foreach (var item in items)
            {
                if (string.IsNullOrEmpty(item.Path)) continue;
                if (Directory.Exists(item.Path) || File.Exists(item.Path)) paths.Add(item.Path);
            }
        }
        catch
        {
            // A provider may not expose a local path; the user can use the picker.
        }
        return paths;
    }

    private async void SendFiles_Click(object sender, RoutedEventArgs e) => await SendFilesPickerAsync(null);

    private async void SendFolder_Click(object sender, RoutedEventArgs e) => await SendFolderPickerAsync();

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_engine is null) return;
        try
        {
            ApplyPeerSnapshot(_engine.RefreshPeers());
            ShowToast("Refreshed", "Looking for nearby Wisp devices.", InfoBarSeverity.Informational, autoClose: true);
        }
        catch (Exception exception)
        {
            ShowToast("Could not refresh", exception.Message, InfoBarSeverity.Error);
        }
    }

    private async void ShowIdentity_Click(object sender, RoutedEventArgs e)
    {
        var self = _engine?.Self;
        if (self is null)
        {
            ShowToast("Wisp is offline", "The local engine is not running.", InfoBarSeverity.Warning);
            return;
        }
        await _dialogGate.WaitAsync();
        try
        {
            var content = new StackPanel { Spacing = 12, Width = 500 };
            content.Children.Add(new TextBlock
            {
                Text = "Share these three values with the other device to connect without discovery.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.7,
                FontSize = 13
            });
            content.Children.Add(LabeledValue("Device name", self.Name));
            content.Children.Add(LabeledValue("Peer ID", self.Id, monospace: true));
            content.Children.Add(LabeledValue("Port", self.Port.ToString(), monospace: true));
            content.Children.Add(LabeledValue("TLS fingerprint", self.Fingerprint, monospace: true));
            var dialog = new ContentDialog
            {
                Title = "This device",
                Content = content,
                PrimaryButtonText = "Copy peer ID",
                CloseButtonText = "Close",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = RootGrid.XamlRoot
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                CopyText(self.Id);
                ShowToast("Copied", "Peer ID is on the clipboard.", InfoBarSeverity.Success);
            }
        }
        finally
        {
            _dialogGate.Release();
        }
    }

    private async void AddManualPeer_Click(object sender, RoutedEventArgs e)
    {
        if (_engine is null)
        {
            ShowToast("Wisp is offline", "The engine must be running to add a device.", InfoBarSeverity.Warning);
            return;
        }
        await _dialogGate.WaitAsync();
        try
        {
            var stack = new StackPanel { Spacing = 10, Width = 440 };
            stack.Children.Add(new TextBlock
            {
                Text = "On the other device, open Devices and read its peer ID, port, and TLS fingerprint. Both devices must be able to reach each other over the network.",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Opacity = 0.7
            });
            var name = new TextBox { Header = "Name (optional)", PlaceholderText = "Pixel 9" };
            var host = new TextBox { Header = "Host or IP", PlaceholderText = "192.168.1.42" };
            var port = new TextBox { Header = "Port", PlaceholderText = "61700" };
            var fingerprint = new TextBox
            {
                Header = "TLS fingerprint (64 hex characters)",
                PlaceholderText = "a1b2c3…",
                FontFamily = new FontFamily("Consolas")
            };
            var errorText = new TextBlock { Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 113, 104)), FontSize = 12, TextWrapping = TextWrapping.Wrap };
            stack.Children.Add(name);
            stack.Children.Add(host);
            stack.Children.Add(port);
            stack.Children.Add(fingerprint);
            stack.Children.Add(errorText);

            var dialog = new ContentDialog
            {
                Title = "Connect by address",
                Content = stack,
                PrimaryButtonText = "Add device",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = RootGrid.XamlRoot
            };
            dialog.PrimaryButtonClick += (_, args) =>
            {
                var hostValue = host.Text.Trim();
                var fingerprintValue = fingerprint.Text.Trim().ToLowerInvariant();
                if (string.IsNullOrEmpty(hostValue))
                {
                    args.Cancel = true;
                    errorText.Text = "Enter the other device’s host or IP address.";
                    return;
                }
                if (!int.TryParse(port.Text.Trim(), out var portValue) || portValue is <= 0 or > 65535)
                {
                    args.Cancel = true;
                    errorText.Text = "Enter the port shown on the other device (1–65535).";
                    return;
                }
                if (!IsHex(fingerprintValue, 64))
                {
                    args.Cancel = true;
                    errorText.Text = "The TLS fingerprint must be exactly 64 hexadecimal characters.";
                    return;
                }
                var peer = new PeerInfo
                {
                    Id = fingerprintValue[..32],
                    Name = string.IsNullOrWhiteSpace(name.Text) ? hostValue : name.Text.Trim(),
                    Host = hostValue,
                    Port = portValue,
                    Fingerprint = fingerprintValue,
                    Platform = "unknown",
                    Online = true
                };
                if (!_engine.AddPeer(peer))
                {
                    args.Cancel = true;
                    errorText.Text = "The engine rejected that address. Check the values and try again.";
                    return;
                }
                ApplyPeer(new PeerEventArgs(peer, true));
                ShowToast("Device added", $"{peer.Name} is ready for a direct connection.", InfoBarSeverity.Success);
            };
            await dialog.ShowAsync();
        }
        finally
        {
            _dialogGate.Release();
        }
    }

    private void CancelTransfer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string id || _engine is null) return;
        if (_engine.Cancel(id)) ShowToast("Cancelling", "The transfer is stopping.", InfoBarSeverity.Informational);
    }

    private void OpenCompletedFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string path) return;
        try
        {
            var target = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(target) && Directory.Exists(target))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{target}\"") { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            ShowToast("Could not open the folder", exception.Message, InfoBarSeverity.Error);
        }
    }

    private void ClearFinished_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _transfers.Where(t => !t.IsActive).ToList())
        {
            _transfers.Remove(item);
            _transferById.Remove(item.TransferId);
        }
        UpdateEmptyStates();
    }

    private async void ApplySettings_Click(object sender, RoutedEventArgs e)
    {
        if (_restarting) return;
        var name = DeviceNameBox.Text.Trim();
        _settings.SoundsEnabled = SoundsToggle.IsOn;
        _sounds.Enabled = SoundsToggle.IsOn;
        var saveDirectory = SaveDirectoryBox.Text.Trim();
        if (string.IsNullOrEmpty(name))
        {
            ShowToast("Name required", "Give this device a name.", InfoBarSeverity.Warning);
            return;
        }
        if (string.IsNullOrEmpty(saveDirectory))
        {
            ShowToast("Folder required", "Choose a folder for received files.", InfoBarSeverity.Warning);
            return;
        }
        if (_transfers.Any(t => t.IsActive))
        {
            await _dialogGate.WaitAsync();
            try
            {
                var dialog = new ContentDialog
                {
                    Title = "Restart the engine?",
                    Content = "Active transfers will stop. You can start them again after saving.",
                    PrimaryButtonText = "Save and restart",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = RootGrid.XamlRoot
                };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            }
            finally
            {
                _dialogGate.Release();
            }
        }

        try
        {
            Directory.CreateDirectory(saveDirectory);
        }
        catch (Exception exception)
        {
            ShowToast("Can’t use that folder", exception.Message, InfoBarSeverity.Error);
            return;
        }

        _settings.DeviceName = name;
        _settings.SaveDirectory = saveDirectory;
        _settings.AutoAcceptTrusted = AutoAcceptTrustedToggle.IsOn;
        _settingsStore.Save(_settings);
        SettingsStatusText.Text = "Restarting engine…";
        await StartEngineAsync();
        SettingsStatusText.Text = "Saved.";
        ShowToast("Settings saved", "Wisp restarted with the new device settings.", InfoBarSeverity.Success);
    }

    private async void BrowseSaveDirectory_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.Downloads };
            picker.FileTypeFilter.Add("*");
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null && !string.IsNullOrEmpty(folder.Path))
                SaveDirectoryBox.Text = folder.Path;
        }
        catch (Exception exception)
        {
            ShowToast("Could not open the folder picker", exception.Message, InfoBarSeverity.Error);
        }
    }

    private void OpenFirewallSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("control.exe", "firewall.cpl") { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            ShowToast("Could not open firewall settings", exception.Message, InfoBarSeverity.Error);
        }
    }

    private void CopyPeerId_Click(object sender, RoutedEventArgs e)
    {
        var id = _engine?.Self?.Id;
        if (string.IsNullOrEmpty(id)) return;
        CopyText(id);
        ShowToast("Copied", "Peer ID is on the clipboard.", InfoBarSeverity.Success);
    }

    private void TrustedAutoAccept_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox checkBox || checkBox.Tag is not string fingerprint) return;
        _trustStore.SetAutoAccept(fingerprint, checkBox.IsChecked == true);
    }

    private void RemoveTrusted_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string fingerprint) return;
        _trustStore.Remove(fingerprint);
        RefreshTrusted();
    }

    private void RefreshTrusted()
    {
        _trusted.Clear();
        foreach (var peer in _trustStore.Items) _trusted.Add(new TrustedDeviceItem(peer));
        TrustedCountText.Text = _trusted.Count == 0 ? "" :
            _trusted.Count == 1 ? "1 device" : $"{_trusted.Count} devices";
        NoTrustedCard.Visibility = _trusted.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TrustedRepeater.Visibility = _trusted.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var tag = args.SelectedItemContainer?.Tag as string ?? "nearby";
        ShowSection(tag);
    }

    private void UpdateHeaderGauge()
    {
        var active = _transfers.Where(t => t.IsActive).ToList();
        if (active.Count > 0)
        {
            var total = active.Sum(t => t.BytesTotal);
            var done = active.Sum(t => t.BytesDone);
            var files = active.Sum(t => t.FilesTotal);
            var pct = total <= 0 ? 0 : done * 100.0 / total;
            HeaderGauge.Visibility = Visibility.Visible;
            HeaderStatusDot.Visibility = Visibility.Collapsed;
            HeaderGauge.IsIndeterminate = total <= 0;
            HeaderGauge.Value = Math.Clamp(pct, 0, 100);
            HeaderGauge.IsActive = true;
            HeaderStatusText.Text = files == 1
                ? $"1 file · {pct:0}%"
                : $"{files} files · {pct:0}%";
        }
        else
        {
            HeaderGauge.Visibility = Visibility.Collapsed;
            HeaderGauge.IsActive = false;
            HeaderStatusDot.Visibility = Visibility.Visible;
            HeaderStatusText.Text = _engine?.Self is null ? "offline" :
                _peers.Count == 0 ? "visible nearby" :
                _peers.Count == 1 ? "1 nearby" : $"{_peers.Count} nearby";
        }
    }

    private void RefreshHistory()
    {
        // History entries appear as finished items in the transfers list, newest
        // first, so past activity is visible after a restart.
        foreach (var entry in _historyStore.Items.Take(20))
        {
            if (_transferById.ContainsKey(entry.Id)) continue;
            var item = new TransferItem(entry.Id, entry.PeerName, entry.Direction);
            if (entry.Succeeded)
            {
                item.MarkComplete(new TransferCompletion
                {
                    TransferId = entry.Id,
                    Direction = entry.Direction,
                    PeerName = entry.PeerName,
                    BytesTotal = entry.Bytes,
                    FilesTotal = entry.Files,
                    SavedPath = entry.SavedPath
                });
            }
            else
            {
                item.MarkFailed(new TransferFailure
                {
                    TransferId = entry.Id,
                    Direction = entry.Direction,
                    PeerName = entry.PeerName,
                    Message = entry.Message
                });
            }
            _transferById[entry.Id] = item;
            _transfers.Insert(0, item);
        }
        UpdateEmptyStates();
    }

    private void SetupTrayIcon()
    {
        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
            if (!File.Exists(iconPath)) return;
            TrayIcon.IconSource = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(
                new Uri(iconPath));
            TrayIcon.ToolTipText = "Wisp — direct file transfer";
            TrayIcon.Visibility = Visibility.Visible;
        }
        catch
        {
            // Tray support is decorative; the app works without it.
        }
    }

    private void TrayQuit_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void TrayIcon_Clicked(object sender, RoutedEventArgs e)
    {
        try
        {
            AppWindow.Show();
            Activate();
        }
        catch
        {
        }
    }

    private void HeaderActivity_Click(object sender, RoutedEventArgs e)
    {
        NavView.SelectedItem = TransfersNavItem;
        ShowSection("transfers");
    }

    private void ShowSection(string tag)
    {
        NearbyView.Visibility = tag == "nearby" ? Visibility.Visible : Visibility.Collapsed;
        TransfersView.Visibility = tag == "transfers" ? Visibility.Visible : Visibility.Collapsed;
        DevicesView.Visibility = tag == "devices" ? Visibility.Visible : Visibility.Collapsed;
        SettingsView.Visibility = tag == "settings" ? Visibility.Visible : Visibility.Collapsed;
        if (tag != "nearby") DropOverlay.Visibility = Visibility.Collapsed;
    }

    private void StartPulseAnimation()
    {
        try
        {
            if (!new UISettings().AnimationsEnabled) return;
            var visual = ElementCompositionPreview.GetElementVisual(PresencePulse);
            var compositor = visual.Compositor;
            visual.CenterPoint = new Vector3(52, 52, 0);
            var scale = compositor.CreateVector3KeyFrameAnimation();
            scale.InsertKeyFrame(0f, new Vector3(0.92f, 0.92f, 1f));
            scale.InsertKeyFrame(0.5f, new Vector3(1.08f, 1.08f, 1f));
            scale.InsertKeyFrame(1f, new Vector3(0.92f, 0.92f, 1f));
            scale.Duration = TimeSpan.FromSeconds(3.8);
            scale.IterationBehavior = AnimationIterationBehavior.Forever;
            visual.StartAnimation("Scale", scale);
            var opacity = compositor.CreateScalarKeyFrameAnimation();
            opacity.InsertKeyFrame(0f, 0.5f);
            opacity.InsertKeyFrame(0.5f, 0.14f);
            opacity.InsertKeyFrame(1f, 0.5f);
            opacity.Duration = TimeSpan.FromSeconds(3.8);
            opacity.IterationBehavior = AnimationIterationBehavior.Forever;
            visual.StartAnimation("Opacity", opacity);
        }
        catch
        {
            // Decorative only.
        }
    }

    private void ShowToast(string title, string message, InfoBarSeverity severity = InfoBarSeverity.Informational,
        bool autoClose = true)
    {
        _toastTimer?.Stop();
        ToastBar.Title = title;
        ToastBar.Message = message;
        ToastBar.Severity = severity;
        ToastBar.IsOpen = true;
        if (!autoClose) return;
        _toastTimer = _dispatcher.CreateTimer();
        _toastTimer.Interval = TimeSpan.FromSeconds(6);
        _toastTimer.IsRepeating = false;
        _toastTimer.Tick += (_, _) =>
        {
            ToastBar.IsOpen = false;
            _toastTimer?.Stop();
        };
        _toastTimer.Start();
    }

    private void CopyText(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }

    private void TryBringToFront()
    {
        try
        {
            AppWindow.Show();
            Activate();
        }
        catch
        {
            // The window can still be interacted with from the taskbar.
        }
    }

    private static StackPanel LabeledValue(string label, string value, bool monospace = false)
    {
        var panel = new StackPanel { Spacing = 3 };
        panel.Children.Add(new TextBlock { Text = label, FontSize = 10, Opacity = 0.6, CharacterSpacing = 70 });
        panel.Children.Add(new TextBlock
        {
            Text = value,
            FontSize = monospace ? 12 : 14,
            FontFamily = monospace ? new FontFamily("Consolas") : new FontFamily("Segoe UI Variable Text"),
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true
        });
        return panel;
    }

    private static bool IsHex(string value, int length)
    {
        if (value.Length != length) return false;
        return value.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'));
    }

    private static string InitialsOf(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "?";
        if (parts.Length == 1) return parts[0][..Math.Min(2, parts[0].Length)].ToUpperInvariant();
        return string.Concat(parts.Take(2).Select(p => char.ToUpperInvariant(p[0])));
    }
}

public sealed class OfferDialog
{
    public OfferDialog(ContentDialog dialog, CheckBox trustCheckBox)
    {
        Dialog = dialog;
        TrustCheckBox = trustCheckBox;
    }

    public ContentDialog Dialog { get; }
    public CheckBox TrustCheckBox { get; }
}
