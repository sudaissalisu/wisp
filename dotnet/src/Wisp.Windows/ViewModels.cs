using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Wisp.Core;
using Windows.UI;

namespace Wisp.Windows;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    protected void Raise([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public static class PeerPalette
{
    private static readonly (string Start, string End)[] Colors =
    [
        ("#5B8DEF", "#9B6BFF"),
        ("#45E3B4", "#38BDF8"),
        ("#FF9A62", "#FF6B8A"),
        ("#FFD166", "#FF8C42"),
        ("#A78BFA", "#67E8F9"),
        ("#F472B6", "#FB923C"),
        ("#34D399", "#60A5FA"),
        ("#F87171", "#FBBF24")
    ];

    public static (Color Start, Color End) For(string id)
    {
        uint hash = 2166136261;
        foreach (var character in id)
            hash = (hash ^ character) * 16777619;
        var pair = Colors[hash % (uint)Colors.Length];
        return (ParseColor(pair.Start), ParseColor(pair.End));
    }

    public static Brush BrushFor(string id)
    {
        var (start, end) = For(id);
        return new LinearGradientBrush
        {
            StartPoint = new global::Windows.Foundation.Point(0, 0),
            EndPoint = new global::Windows.Foundation.Point(1, 1),
            GradientStops =
            {
                new GradientStop { Color = start, Offset = 0 },
                new GradientStop { Color = end, Offset = 1 }
            }
        };
    }

    private static Color ParseColor(string value)
    {
        var hex = value.TrimStart('#');
        return Color.FromArgb(
            255,
            Convert.ToByte(hex[..2], 16),
            Convert.ToByte(hex.Substring(2, 2), 16),
            Convert.ToByte(hex.Substring(4, 2), 16));
    }
}

public sealed class PeerItem : ObservableObject
{
    private PeerInfo _info;

    public PeerItem(PeerInfo info)
    {
        _info = info;
        AvatarBrush = PeerPalette.BrushFor(info.Id);
    }

    public PeerInfo Info => _info;
    public string Id => _info.Id;
    public string Name => _info.Name;
    public string Initials => _info.Initials;
    public Brush AvatarBrush { get; }
    public string PlatformLabel => _info.IsAndroid ? "Android" : _info.IsWindows() ? "Windows PC" : "Nearby device";
    public string PlatformGlyph => _info.IsAndroid ? "\uE8EA" : "\uE7F4";
    public string Subtitle => $"{PlatformLabel}  ·  ready to receive";
    public string Fingerprint => _info.Fingerprint;
    public bool IsOnline => _info.Online;

    public void Update(PeerInfo info)
    {
        _info = info;
        Raise(nameof(Info));
        Raise(nameof(Name));
        Raise(nameof(Initials));
        Raise(nameof(PlatformLabel));
        Raise(nameof(PlatformGlyph));
        Raise(nameof(Subtitle));
        Raise(nameof(Fingerprint));
        Raise(nameof(IsOnline));
    }
}

public static class PeerInfoExtensions
{
    public static bool IsWindows(this PeerInfo peer) =>
        peer.Platform.Equals("windows", StringComparison.OrdinalIgnoreCase);
}

public sealed class TransferItem : ObservableObject
{
    private double _percent;
    private string _state = "waiting";
    private string _status = "Waiting for acceptance";
    private string _speedText = "";
    private string _remainingText = "";
    private long _bytesDone;
    private long _bytesTotal;
    private int _filesTotal;
    private string? _savedPath;

    public TransferItem(string transferId, string peerName, string direction)
    {
        TransferId = transferId;
        PeerName = peerName;
        Direction = direction;
    }

    public string TransferId { get; }
    public string PeerName { get; }
    public string Direction { get; }
    public bool IsIncoming => Direction.Equals("receive", StringComparison.OrdinalIgnoreCase);
    public string Title => IsIncoming ? $"From {PeerName}" : $"To {PeerName}";
    public string DirectionGlyph => IsIncoming ? "\uE896" : "\uE898";
    public string DirectionLabel => IsIncoming ? "INCOMING" : "OUTGOING";
    public double Percent { get => _percent; private set => SetProperty(ref _percent, value); }
    public string State { get => _state; private set => SetProperty(ref _state, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string SpeedText { get => _speedText; private set => SetProperty(ref _speedText, value); }
    public string RemainingText { get => _remainingText; private set => SetProperty(ref _remainingText, value); }
    public string? SavedPath { get => _savedPath; private set => SetProperty(ref _savedPath, value); }
    public long BytesDone => _bytesDone;
    public long BytesTotal { get => _bytesTotal; private set => SetProperty(ref _bytesTotal, value); }
    public int FilesTotal { get => _filesTotal; private set => SetProperty(ref _filesTotal, value); }
    public string SizeText => TransferOffer.FormatBytes(BytesTotal);
    public bool IsActive => _state is "waiting" or "preparing" or "sending" or "receiving" or "verifying";
    public Visibility ProgressVisibility => IsActive ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CancelVisibility => IsActive ? Visibility.Visible : Visibility.Collapsed;
    public Visibility OpenFolderVisibility => !IsActive && SavedPath is not null
        ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CompleteGlyphVisibility => !IsActive ? Visibility.Visible : Visibility.Collapsed;
    public string StateGlyph => State == "completed" ? "\uE73E" : State == "failed" ? "\uE783" : "\uE711";
    public Brush StateBrush => State switch
    {
        "completed" => new SolidColorBrush(Color.FromArgb(255, 69, 227, 180)),
        "failed" => new SolidColorBrush(Color.FromArgb(255, 255, 113, 104)),
        _ => new SolidColorBrush(Color.FromArgb(255, 91, 141, 239))
    };

    public void ApplyProgress(TransferProgress progress)
    {
        BytesTotal = (long)progress.BytesTotal;
        _bytesDone = (long)progress.BytesDone;
        _filesTotal = progress.FilesTotal;
        Raise(nameof(BytesDone));
        Raise(nameof(FilesTotal));
        Percent = progress.BytesTotal <= 0 ? 0 : Math.Clamp(progress.BytesDone * 100.0 / progress.BytesTotal, 0, 100);
        State = string.IsNullOrWhiteSpace(progress.State) ? State : progress.State;
        Status = State switch
        {
            "preparing" => "Preparing files",
            "waiting" => "Waiting for acceptance",
            "sending" => $"{progress.FilesDone}/{progress.FilesTotal} files sent",
            "receiving" => $"{progress.FilesDone}/{progress.FilesTotal} files received",
            _ => Status
        };
        SpeedText = progress.SpeedBps > 0 ? progress.SpeedText : "";
        RemainingText = progress.RemainingText;
        Raise(nameof(Percent));
        Raise(nameof(Status));
        Raise(nameof(SpeedText));
        Raise(nameof(RemainingText));
        Raise(nameof(SizeText));
        Raise(nameof(IsActive));
        Raise(nameof(ProgressVisibility));
        Raise(nameof(CancelVisibility));
        Raise(nameof(OpenFolderVisibility));
        Raise(nameof(CompleteGlyphVisibility));
        Raise(nameof(StateGlyph));
        Raise(nameof(StateBrush));
    }

    public void MarkComplete(TransferCompletion completion)
    {
        Percent = 100;
        State = "completed";
        Status = IsIncoming ? "Received" : "Delivered";
        SpeedText = "";
        RemainingText = "";
        BytesTotal = (long)completion.BytesTotal;
        _filesTotal = completion.FilesTotal;
        SavedPath = completion.SavedPath;
        Raise(nameof(BytesDone));
        Raise(nameof(FilesTotal));
        Raise(nameof(Percent));
        Raise(nameof(Status));
        Raise(nameof(SpeedText));
        Raise(nameof(RemainingText));
        Raise(nameof(SizeText));
        Raise(nameof(IsActive));
        Raise(nameof(ProgressVisibility));
        Raise(nameof(CancelVisibility));
        Raise(nameof(OpenFolderVisibility));
        Raise(nameof(CompleteGlyphVisibility));
        Raise(nameof(StateGlyph));
        Raise(nameof(StateBrush));
    }

    public void MarkFailed(TransferFailure failure)
    {
        State = "failed";
        Status = string.IsNullOrWhiteSpace(failure.Message) ? "Transfer failed" : failure.Message;
        SpeedText = "";
        RemainingText = "";
        Raise(nameof(State));
        Raise(nameof(Status));
        Raise(nameof(SpeedText));
        Raise(nameof(RemainingText));
        Raise(nameof(IsActive));
        Raise(nameof(ProgressVisibility));
        Raise(nameof(CancelVisibility));
        Raise(nameof(OpenFolderVisibility));
        Raise(nameof(CompleteGlyphVisibility));
        Raise(nameof(StateGlyph));
        Raise(nameof(StateBrush));
    }
}

public sealed class TrustedDeviceItem
{
    public TrustedDeviceItem(TrustedPeer peer)
    {
        Peer = peer;
        AvatarBrush = PeerPalette.BrushFor(peer.Id);
    }

    public TrustedPeer Peer { get; }
    public string Id => Peer.Id;
    public string Name => Peer.Name;
    public string Fingerprint => Peer.Fingerprint;
    public string FingerprintShort => Peer.Fingerprint.Length > 20
        ? $"{Peer.Fingerprint[..10]}…{Peer.Fingerprint[^10..]}"
        : Peer.Fingerprint;
    public string Initials => InitialsOf(Peer.Name);
    public bool AutoAccept => Peer.AutoAccept;
    public Brush AvatarBrush { get; }

    private static string InitialsOf(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "?";
        if (parts.Length == 1) return parts[0][..Math.Min(2, parts[0].Length)].ToUpperInvariant();
        return string.Concat(parts.Take(2).Select(p => char.ToUpperInvariant(p[0])));
    }
}
