using System.Text.Json;

namespace Wisp.Core;

public sealed record TrustedPeer
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Fingerprint { get; init; } = "";
    public bool AutoAccept { get; init; }
    public DateTimeOffset LastSeenUtc { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Public-key trust pins for devices the user has explicitly accepted. This is
/// deliberately a small local file: no account, cloud, or certificate authority.
/// </summary>
public sealed class TrustStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _gate = new();
    private readonly string _path;
    private readonly List<TrustedPeer> _peers = [];

    public TrustStore(string path)
    {
        _path = path;
        Load();
    }

    public IReadOnlyList<TrustedPeer> Items
    {
        get
        {
            lock (_gate) return _peers.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        }
    }

    public TrustedPeer? Find(string fingerprint)
    {
        if (string.IsNullOrWhiteSpace(fingerprint)) return null;
        lock (_gate)
            return _peers.FirstOrDefault(p => p.Fingerprint.Equals(fingerprint, StringComparison.OrdinalIgnoreCase));
    }

    public TrustedPeer? FindById(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        lock (_gate) return _peers.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
    }

    public TrustedPeer Trust(PeerInfo peer, bool autoAccept = false)
    {
        var trusted = new TrustedPeer
        {
            Id = peer.Id,
            Name = peer.Name,
            Fingerprint = peer.Fingerprint,
            AutoAccept = autoAccept,
            LastSeenUtc = DateTimeOffset.UtcNow
        };
        lock (_gate)
        {
            _peers.RemoveAll(p => p.Fingerprint.Equals(trusted.Fingerprint, StringComparison.OrdinalIgnoreCase));
            _peers.Add(trusted);
        }
        Save();
        return trusted;
    }

    public void SetAutoAccept(string fingerprint, bool autoAccept)
    {
        lock (_gate)
        {
            var index = _peers.FindIndex(p => p.Fingerprint.Equals(fingerprint, StringComparison.OrdinalIgnoreCase));
            if (index < 0) return;
            _peers[index] = _peers[index] with { AutoAccept = autoAccept };
        }
        Save();
    }

    public void Remove(string fingerprint)
    {
        lock (_gate)
            _peers.RemoveAll(p => p.Fingerprint.Equals(fingerprint, StringComparison.OrdinalIgnoreCase));
        Save();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var loaded = JsonSerializer.Deserialize<List<TrustedPeer>>(
                File.ReadAllText(_path), JsonOptions);
            if (loaded is null) return;
            lock (_gate) _peers.AddRange(loaded.Where(p => !string.IsNullOrWhiteSpace(p.Fingerprint)));
        }
        catch
        {
            // A corrupt trust file must not prevent the app from starting.
        }
    }

    private void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            TrustedPeer[] snapshot;
            lock (_gate) snapshot = _peers.ToArray();
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(snapshot, JsonOptions));
            File.Move(temporary, _path, true);
        }
        catch
        {
            // The transfer itself should still work if preferences cannot persist.
        }
    }
}
