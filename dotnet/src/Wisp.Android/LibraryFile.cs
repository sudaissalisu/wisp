using Wisp.Core;

namespace Wisp.Android;

public enum LibraryKind
{
    Image,
    Video,
    Audio,
    Document,
    Other
}

/// <summary>A received file shown in the Files tab and opened by the viewer.</summary>
public sealed class LibraryFile
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".heic", ".heif"
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mkv", ".webm", ".3gp", ".3g2", ".avi", ".mov", ".ts"
    };

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".m4a", ".aac", ".ogg", ".opus", ".wav", ".flac", ".amr", ".mid", ".midi"
    };

    private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".md",
        ".csv", ".rtf", ".epub", ".json", ".xml", ".html", ".htm", ".apk", ".zip",
        ".rar", ".7z", ".tar", ".gz"
    };

    private LibraryFile(string path, LibraryKind kind, long size, DateTime modified)
    {
        Path = path;
        Kind = kind;
        Size = size;
        Modified = modified;
    }

    public string Path { get; }
    public LibraryKind Kind { get; }
    public long Size { get; }
    public DateTime Modified { get; }
    public string FileName => System.IO.Path.GetFileName(Path);
    public bool IsMedia => Kind is LibraryKind.Image or LibraryKind.Video or LibraryKind.Audio;

    public string Glyph => Kind switch
    {
        LibraryKind.Image => "\uE413",
        LibraryKind.Video => "\uE02C",
        LibraryKind.Audio => "\uE3A1",
        LibraryKind.Document => "\uE873",
        _ => "\uE24D"
    };

    public string KindText => Kind switch
    {
        LibraryKind.Image => "Photo",
        LibraryKind.Video => "Video",
        LibraryKind.Audio => "Music",
        LibraryKind.Document => "Document",
        _ => "File"
    };

    public string MetaText =>
        $"{KindText} · {TransferOffer.FormatBytes(Size)} · {ModifiedText}";

    public string ModifiedText
    {
        get
        {
            var local = Modified.ToLocalTime();
            var now = DateTimeOffset.Now;
            if (local.Date == now.Date) return local.ToString("HH:mm");
            if (local.Date == now.Date.AddDays(-1)) return $"Yesterday {local:HH:mm}";
            return local.ToString("MMM d, HH:mm");
        }
    }

    public static LibraryKind KindOf(string path) =>
        Classify(System.IO.Path.GetExtension(path));

    public static LibraryKind Classify(string extension)
    {
        if (ImageExtensions.Contains(extension)) return LibraryKind.Image;
        if (VideoExtensions.Contains(extension)) return LibraryKind.Video;
        if (AudioExtensions.Contains(extension)) return LibraryKind.Audio;
        if (DocumentExtensions.Contains(extension)) return LibraryKind.Document;
        return LibraryKind.Other;
    }

    public static bool CanPreview(string path)
    {
        var kind = KindOf(path);
        return kind is LibraryKind.Image or LibraryKind.Video or LibraryKind.Audio;
    }

    public static Task<List<LibraryFile>> ScanAsync(string root, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            var files = new List<LibraryFile>();
            if (!Directory.Exists(root)) return files;
            try
            {
                foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var name = System.IO.Path.GetFileName(path);
                    if (name.Contains(".wisp-", StringComparison.OrdinalIgnoreCase) ||
                        name.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    try
                    {
                        var info = new FileInfo(path);
                        files.Add(new LibraryFile(path, KindOf(path), info.Length, info.LastWriteTime));
                    }
                    catch
                    {
                        // A file removed mid-scan should not break the whole list.
                    }
                }
            }
            catch
            {
                // The external directory can disappear if the user revokes access.
            }
            return files
                .OrderByDescending(file => file.Modified)
                .Take(500)
                .ToList();
        }, cancellationToken);
    }
}
