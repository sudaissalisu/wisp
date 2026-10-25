using System.Text.Json.Serialization;

namespace Wisp.Core;

public sealed record WispConfig
{
    public required string Name { get; init; }
    public required string SaveDirectory { get; init; }
    public required string IdentityDirectory { get; init; }
    public int ListenPort { get; init; }
    public int DiscoveryPort { get; init; } = 47778;
    public bool AutoAccept { get; init; }
    public int DataChannels { get; init; } = 4;
    public int ConflictPolicy { get; init; }
}

public sealed record PeerInfo
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("platform")] public string Platform { get; init; } = "";
    [JsonPropertyName("host")] public string Host { get; init; } = "";
    [JsonPropertyName("port")] public int Port { get; init; }
    [JsonPropertyName("fingerprint")] public string Fingerprint { get; init; } = "";
    [JsonPropertyName("online")] public bool Online { get; init; }

    [JsonIgnore]
    public bool IsAndroid => Platform.Equals("android", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public string Initials
    {
        get
        {
            var parts = Name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return "?";
            if (parts.Length == 1) return parts[0][..Math.Min(2, parts[0].Length)].ToUpperInvariant();
            return string.Concat(parts.Take(2).Select(p => char.ToUpperInvariant(p[0])));
        }
    }
}

public sealed record OfferedFile
{
    [JsonPropertyName("path")] public string Path { get; init; } = "";
    [JsonPropertyName("size")] public long Size { get; init; }
}

public sealed record TransferOffer
{
    [JsonPropertyName("offer_id")] public string OfferId { get; init; } = "";
    [JsonPropertyName("transfer_id")] public string TransferId { get; init; } = "";
    [JsonPropertyName("peer_id")] public string PeerId { get; init; } = "";
    [JsonPropertyName("peer_name")] public string PeerName { get; init; } = "";
    [JsonPropertyName("fingerprint")] public string Fingerprint { get; init; } = "";
    [JsonPropertyName("pairing_code")] public string PairingCode { get; init; } = "";
    [JsonPropertyName("file_count")] public int FileCount { get; init; }
    [JsonPropertyName("total_bytes")] public long TotalBytes { get; init; }
    [JsonPropertyName("save_dir")] public string SaveDir { get; init; } = "";
    [JsonPropertyName("files")] public IReadOnlyList<OfferedFile> Files { get; init; } = [];
    [JsonPropertyName("files_truncated")] public int FilesTruncated { get; init; }

    [JsonIgnore] public string SizeText => FormatBytes(TotalBytes);
    [JsonIgnore] public string FileCountText => FileCount == 1 ? "1 item" : $"{FileCount:N0} items";

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.0} {units[unit]}";
    }
}

public sealed record TransferProgress
{
    [JsonPropertyName("transfer_id")] public string TransferId { get; init; } = "";
    [JsonPropertyName("direction")] public string Direction { get; init; } = "";
    [JsonPropertyName("peer_id")] public string PeerId { get; init; } = "";
    [JsonPropertyName("peer_name")] public string PeerName { get; init; } = "";
    [JsonPropertyName("state")] public string State { get; init; } = "";
    [JsonPropertyName("bytes_done")] public long BytesDone { get; init; }
    [JsonPropertyName("bytes_total")] public long BytesTotal { get; init; }
    [JsonPropertyName("files_done")] public int FilesDone { get; init; }
    [JsonPropertyName("files_total")] public int FilesTotal { get; init; }
    [JsonPropertyName("speed_bps")] public long SpeedBps { get; init; }

    [JsonIgnore] public double Fraction => BytesTotal <= 0 ? 0 : Math.Clamp((double)BytesDone / BytesTotal, 0, 1);
    [JsonIgnore] public bool IsIncoming => Direction.Equals("receive", StringComparison.OrdinalIgnoreCase);
    [JsonIgnore] public string SpeedText => $"{TransferOffer.FormatBytes(SpeedBps)}/s";
    [JsonIgnore] public string RemainingText
    {
        get
        {
            if (SpeedBps <= 0 || BytesTotal <= BytesDone) return "";
            var seconds = (BytesTotal - BytesDone) / (double)SpeedBps;
            return seconds < 60 ? $"{seconds:0}s left" : $"{seconds / 60:0}m left";
        }
    }
}

public sealed record TransferCompletion
{
    [JsonPropertyName("transfer_id")] public string TransferId { get; init; } = "";
    [JsonPropertyName("direction")] public string Direction { get; init; } = "";
    [JsonPropertyName("peer_id")] public string PeerId { get; init; } = "";
    [JsonPropertyName("peer_name")] public string PeerName { get; init; } = "";
    [JsonPropertyName("bytes_total")] public long BytesTotal { get; init; }
    [JsonPropertyName("files_total")] public int FilesTotal { get; init; }
    [JsonPropertyName("saved_path")] public string? SavedPath { get; init; }

    [JsonIgnore] public bool IsIncoming => Direction.Equals("receive", StringComparison.OrdinalIgnoreCase);
}

public sealed record TransferFailure
{
    [JsonPropertyName("transfer_id")] public string TransferId { get; init; } = "";
    [JsonPropertyName("direction")] public string Direction { get; init; } = "";
    [JsonPropertyName("peer_id")] public string PeerId { get; init; } = "";
    [JsonPropertyName("peer_name")] public string PeerName { get; init; } = "";
    [JsonPropertyName("message")] public string Message { get; init; } = "";
    [JsonPropertyName("code")] public int Code { get; init; }

    [JsonIgnore] public bool IsIncoming => Direction.Equals("receive", StringComparison.OrdinalIgnoreCase);
}

public sealed record PairingInfo
{
    [JsonPropertyName("transfer_id")] public string TransferId { get; init; } = "";
    [JsonPropertyName("peer_id")] public string PeerId { get; init; } = "";
    [JsonPropertyName("peer_name")] public string PeerName { get; init; } = "";
    [JsonPropertyName("code")] public string Code { get; init; } = "";
    [JsonPropertyName("direction")] public string Direction { get; init; } = "";
}

public sealed record PeerEventArgs(PeerInfo Peer, bool Online);
public sealed record LogEventArgs(int Level, string Message);
