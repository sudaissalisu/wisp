using System.ComponentModel;
using Microsoft.Maui.Graphics;
using MauiColor = Microsoft.Maui.Graphics.Color;
using System.Runtime.CompilerServices;
using Wisp.Core;

namespace Wisp.Android;

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

public static class AndroidPalette
{
    private static readonly (string Start, string End)[] Pairs =
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

    public static Brush BrushFor(string id)
    {
        uint hash = 2166136261;
        foreach (var character in id) hash = (hash ^ character) * 16777619;
        var pair = Pairs[hash % (uint)Pairs.Length];
        return new LinearGradientBrush(
            new GradientStopCollection
            {
                new GradientStop(Color.FromArgb(pair.Start), 0f),
                new GradientStop(Color.FromArgb(pair.End), 1f)
            },
            new Point(0, 0), new Point(1, 1));
    }

    public static string InitialsOf(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "?";
        if (parts.Length == 1) return parts[0][..Math.Min(2, parts[0].Length)].ToUpperInvariant();
        return string.Concat(parts.Take(2).Select(p => char.ToUpperInvariant(p[0])));
    }
}

public sealed class PeerItem : ObservableObject
{
    private PeerInfo _info;

    public PeerItem(PeerInfo info)
    {
        _info = info;
        AvatarBrush = AndroidPalette.BrushFor(info.Id);
    }

    public PeerInfo Info => _info;
    public string Id => _info.Id;
    public string Name => _info.Name;
    public string Initials => AndroidPalette.InitialsOf(_info.Name);
    public string Platform => _info.Platform.Equals("windows", StringComparison.OrdinalIgnoreCase)
        ? "Windows PC" : _info.Platform.Equals("android", StringComparison.OrdinalIgnoreCase)
            ? "Android" : "Nearby device";
    public string PlatformGlyph => _info.Platform.Equals("windows", StringComparison.OrdinalIgnoreCase)
        ? "" : _info.Platform.Equals("android", StringComparison.OrdinalIgnoreCase)
            ? "" : "";
    public string Subtitle => $"{Platform} · ready";
    public Brush AvatarBrush { get; }

    public void Update(PeerInfo info)
    {
        _info = info;
        Raise(nameof(Name));
        Raise(nameof(Initials));
        Raise(nameof(Platform));
        Raise(nameof(Subtitle));
    }
}

public sealed class TransferItem : ObservableObject
{
    private double _progress;
    private string _status = "Waiting for acceptance";
    private string _speed = "";
    private bool _active = true;

    public TransferItem(string id, string peerName, string direction)
    {
        Id = id;
        PeerName = peerName;
        Direction = direction;
    }

    public string Id { get; }
    public string PeerName { get; }
    public string Direction { get; }
    public bool IsIncoming => Direction.Equals("receive", StringComparison.OrdinalIgnoreCase);
    public string Title => IsIncoming ? $"From {PeerName}" : $"To {PeerName}";
    public string Glyph => IsIncoming ? "↓" : "↑";
    public double Progress { get => _progress; private set => SetProperty(ref _progress, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string Speed { get => _speed; private set => SetProperty(ref _speed, value); }
    public bool IsActive { get => _active; private set => SetProperty(ref _active, value); }
    public bool ShowCancel => _active;
    public string SizeText { get; private set; } = "";

    public void ApplyProgress(TransferProgress progress)
    {
        Progress = progress.Fraction;
        Status = progress.State switch
        {
            "preparing" => "Preparing files",
            "waiting" => "Waiting for acceptance",
            "sending" => $"{progress.FilesDone}/{progress.FilesTotal} sent",
            "receiving" => $"{progress.FilesDone}/{progress.FilesTotal} received",
            _ => Status
        };
        Speed = progress.SpeedBps > 0 ? $"{progress.SpeedText}  ·  {progress.RemainingText}" : "";
        SizeText = TransferOffer.FormatBytes(progress.BytesTotal);
        IsActive = true;
        Raise(nameof(Speed));
        Raise(nameof(SizeText));
        Raise(nameof(ShowCancel));
    }

    public void Complete(TransferCompletion completion)
    {
        Progress = 1;
        Status = IsIncoming ? "Received" : "Delivered";
        Speed = "";
        SizeText = TransferOffer.FormatBytes(completion.BytesTotal);
        IsActive = false;
        Raise(nameof(Speed));
        Raise(nameof(SizeText));
        Raise(nameof(ShowCancel));
    }

    public void Fail(string message)
    {
        Status = message;
        Speed = "";
        IsActive = false;
        Raise(nameof(ShowCancel));
    }
}

/// <summary>Binds a finished transfer history row.</summary>
public sealed class HistoryItem
{
    public HistoryItem(HistoryEntry entry)
    {
        Entry = entry;
        StateBrush = entry.Succeeded
            ? new SolidColorBrush(MauiColor.FromArgb("#45E3B4"))
            : new SolidColorBrush(MauiColor.FromArgb("#FF7168"));
    }

    public HistoryEntry Entry { get; }
    public string Glyph => Entry.Glyph;
    public string Title => Entry.Title;
    public string StatusText => Entry.StatusText;
    public string SizeText => Entry.SizeText;
    public string WhenText => Entry.WhenText;
    public bool CanOpen => Entry.IsIncoming && Entry.Succeeded &&
                           !string.IsNullOrWhiteSpace(Entry.SavedPath);
    public Brush StateBrush { get; }
}

public sealed class TrustedItem
{
    public TrustedItem(TrustedPeer peer)
    {
        Name = peer.Name;
        Fingerprint = peer.Fingerprint;
        Initials = AndroidPalette.InitialsOf(peer.Name);
        AvatarBrush = AndroidPalette.BrushFor(peer.Id);
        AutoAccept = peer.AutoAccept;
    }

    public string Name { get; }
    public string Fingerprint { get; }
    public string Initials { get; }
    public Brush AvatarBrush { get; }
    public bool AutoAccept { get; }
}
