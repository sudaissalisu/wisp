using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wisp.Core;

public sealed record HistoryEntry
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("direction")] public string Direction { get; init; } = "";
    [JsonPropertyName("peer_name")] public string PeerName { get; init; } = "";
    [JsonPropertyName("peer_id")] public string PeerId { get; init; } = "";
    [JsonPropertyName("bytes")] public long Bytes { get; init; }
    [JsonPropertyName("files")] public int Files { get; init; }
    [JsonPropertyName("state")] public string State { get; init; } = "completed";
    [JsonPropertyName("message")] public string Message { get; init; } = "";
    [JsonPropertyName("saved_path")] public string? SavedPath { get; init; }
    [JsonPropertyName("finished_utc")] public DateTimeOffset FinishedUtc { get; init; } = DateTimeOffset.UtcNow;

    [JsonIgnore] public bool IsIncoming => Direction.Equals("receive", StringComparison.OrdinalIgnoreCase);
    [JsonIgnore] public bool Succeeded => State.Equals("completed", StringComparison.OrdinalIgnoreCase);
    [JsonIgnore] public string Glyph => Succeeded
        ? (IsIncoming ? "\uE5DB" : "\uE5D8")
        : "\uE001";
    [JsonIgnore] public string Title => IsIncoming ? $"From {PeerName}" : $"To {PeerName}";
    [JsonIgnore] public string SizeText => TransferOffer.FormatBytes(Bytes);
    [JsonIgnore] public string WhenText
    {
        get
        {
            var local = FinishedUtc.ToLocalTime();
            var now = DateTimeOffset.Now;
            if (local.Date == now.Date) return local.ToString("HH:mm");
            if (local.Date == now.Date.AddDays(-1)) return $"Yesterday {local:HH:mm}";
            return local.ToString("MMM d, HH:mm");
        }
    }
    [JsonIgnore] public string StatusText => Succeeded
        ? (IsIncoming ? "Received" : "Delivered")
        : (string.IsNullOrWhiteSpace(Message) ? "Failed" : Message);
}

/// <summary>
/// Durable list of finished transfers, newest first, capped at 200 entries.
/// </summary>
public sealed class HistoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private const int Capacity = 200;
    private readonly object _gate = new();
    private readonly string _path;
    private readonly List<HistoryEntry> _entries = [];

    public HistoryStore(string path)
    {
        _path = path;
        Load();
    }

    public IReadOnlyList<HistoryEntry> Items
    {
        get
        {
            lock (_gate) return _entries.ToArray();
        }
    }

    public void Add(HistoryEntry entry)
    {
        lock (_gate)
        {
            _entries.RemoveAll(e => e.Id.Equals(entry.Id, StringComparison.OrdinalIgnoreCase));
            _entries.Insert(0, entry);
            if (_entries.Count > Capacity) _entries.RemoveRange(Capacity, _entries.Count - Capacity);
        }
        Save();
    }

    public void Clear()
    {
        lock (_gate) _entries.Clear();
        Save();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var loaded = JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(_path), JsonOptions);
            if (loaded is null) return;
            lock (_gate) _entries.AddRange(loaded.Where(e => !string.IsNullOrWhiteSpace(e.Id)));
            lock (_gate) _entries.Sort((a, b) => b.FinishedUtc.CompareTo(a.FinishedUtc));
        }
        catch
        {
        }
    }

    private void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            HistoryEntry[] snapshot;
            lock (_gate) snapshot = _entries.ToArray();
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(snapshot, JsonOptions));
            File.Move(temporary, _path, true);
        }
        catch
        {
        }
    }
}
