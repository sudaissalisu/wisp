using System.Text;
namespace Wisp.Core;

public sealed record VoiceCommand(PeerInfo Peer, LibraryFile File, string Transcript);

/// <summary>
/// Turns a small set of natural-language send phrases into a file and a peer.
/// It intentionally runs locally with plain rules; the confirmation dialog is
/// the safety boundary, not the parser.
/// </summary>
public static class VoiceCommandParser
{
    private static readonly string[] MonthNames =
    [
        "january", "february", "march", "april", "may", "june",
        "july", "august", "september", "october", "november", "december"
    ];

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "send", "share", "give", "deliver", "please", "the", "a", "an", "my", "me",
        "to", "for", "from", "of", "this", "that", "these", "those", "file", "files",
        "item", "items", "one", "some", "please", "can", "you", "could", "would"
    };

    public static (VoiceCommand? Command, string Message) Parse(
        string transcript,
        IReadOnlyList<PeerInfo> peers,
        IReadOnlyList<LibraryFile> files)
    {
        var text = Normalize(transcript);
        if (string.IsNullOrWhiteSpace(text))
            return (null, "I didn’t hear a command. Try “Send the video from yesterday to Sara”.");

        if (!ContainsWord(text, "send") && !ContainsWord(text, "share") &&
            !ContainsWord(text, "give") && !ContainsWord(text, "deliver"))
        {
            return (null, "Try a send command: “Send the video from yesterday to Sara”.");
        }

        var (peer, peerError) = MatchPeer(text, peers);
        if (peer is null) return (null, peerError);
        if (files.Count == 0)
            return (null, "There are no received files to send yet.");

        var split = LastIndexOfWord(text, "to");
        if (split < 0) split = LastIndexOfWord(text, "for");
        var descriptor = split >= 0 ? text[..split] : text;
        var (file, fileError) = MatchFile(descriptor, files);
        if (file is null) return (null, fileError);

        return (new VoiceCommand(peer, file, transcript.Trim()), "");
    }

    private static (PeerInfo? Peer, string Message) MatchPeer(
        string text, IReadOnlyList<PeerInfo> peers)
    {
        if (peers.Count == 0)
            return (null, "No devices are nearby. Open Wisp on the other device first.");

        var target = "";
        var separator = "to";
        var toIndex = LastIndexOfWord(text, separator);
        if (toIndex < 0)
        {
            separator = "for";
            toIndex = LastIndexOfWord(text, separator);
        }
        if (toIndex >= 0) target = text[(toIndex + separator.Length)..].Trim();
        if (target.Length == 0)
        {
            // Let "send the video Sara" work when the name is the trailing text.
            foreach (var candidate in peers.OrderByDescending(p => p.Name.Length))
            {
                var name = Normalize(candidate.Name);
                if (name.Length > 2 && text.Contains(name, StringComparison.Ordinal))
                    return (candidate, "");
            }
            if (peers.Count == 1) return (peers[0], "");
            return (null, "Say the device name after “to”, for example “to Sara”.");
        }

        var targetTokens = Tokens(target);
        PeerInfo? best = null;
        var bestScore = 0;
        foreach (var peer in peers)
        {
            var name = Normalize(peer.Name);
            if (name.Length > 0 &&
                (target.Contains(name, StringComparison.Ordinal) ||
                 (targetTokens.Count > 0 &&
                  Tokens(name).All(token => targetTokens.Contains(token)))))
            {
                return (peer, "");
            }

            var peerTokens = Tokens(peer.Name);
            var score = peerTokens.Count(token => targetTokens.Contains(token));
            if (score > bestScore)
            {
                bestScore = score;
                best = peer;
            }
        }
        if (best is not null && bestScore > 0) return (best, "");
        return (null,
            $"I couldn’t match “{target}”. Nearby devices: {string.Join(", ", peers.Select(p => p.Name))}.");
    }

    private static (LibraryFile? File, string Message) MatchFile(
        string text, IReadOnlyList<LibraryFile> files)
    {
        var kind = KindFrom(text);
        var date = DatePredicate(text, out var dateLabel);
        var tokens = Tokens(text)
            .Where(token => !StopWords.Contains(token) &&
                            !MonthNames.Contains(token) &&
                            !IsKindWord(token) &&
                            !IsDateWord(token))
            .ToList();

        var matches = new List<(LibraryFile File, int Score)>();
        foreach (var file in files)
        {
            if (kind is not null && file.Kind != kind) continue;
            if (date is not null && !date(file)) continue;

            var score = 0;
            if (kind is not null) score += 20;
            if (date is not null) score += 12;

            var name = file.FileName.ToLowerInvariant();
            var nameHits = tokens.Count(token => name.Contains(token, StringComparison.Ordinal));
            score += nameHits * 8;

            if (tokens.Count > 0 && nameHits == 0)
            {
                // A named object must actually appear in the filename. "Beach
                // video" should not silently become the newest unrelated video.
                continue;
            }
            if (score > 0) matches.Add((file, score));
        }

        if (matches.Count == 0)
        {
            var descriptor = string.Join(" ", tokens.Count > 0 ? tokens : []);
            if (string.IsNullOrWhiteSpace(descriptor))
                descriptor = kind?.ToString().ToLowerInvariant() ?? "file";
            return (null, $"I couldn’t find a {descriptor} {dateLabel}. Try a file name or a type.");
        }

        var best = matches
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.File.Modified)
            .First().File;
        return (best, "");
    }

    private static LibraryKind? KindFrom(string text)
    {
        if (ContainsAny(text, "video", "movie", "clip", "film")) return LibraryKind.Video;
        if (ContainsAny(text, "photo", "picture", "image", "screenshot")) return LibraryKind.Image;
        if (ContainsAny(text, "audio", "song", "music", "recording", "voice"))
            return LibraryKind.Audio;
        if (ContainsAny(text, "pdf", "document", "doc", "invoice", "receipt", "contract"))
            return LibraryKind.Document;
        return null;
    }

    private static Func<LibraryFile, bool>? DatePredicate(string text, out string label)
    {
        var today = DateTime.Today;
        if (ContainsAny(text, "yesterday", "last night"))
        {
            label = "from yesterday";
            return file => file.Modified.Date == today.AddDays(-1);
        }
        if (ContainsAny(text, "today", "this morning", "tonight"))
        {
            label = "from today";
            return file => file.Modified.Date == today;
        }
        if (ContainsAny(text, "last week", "this week"))
        {
            label = "from this week";
            return file => file.Modified >= today.AddDays(-7);
        }
        if (ContainsAny(text, "last month", "this month"))
        {
            label = "from this month";
            return file => file.Modified >= today.AddDays(-31);
        }
        for (var i = 0; i < MonthNames.Length; i++)
        {
            if (ContainsWord(text, MonthNames[i]))
            {
                var month = i + 1;
                label = $"from {MonthNames[i]}";
                return file => file.Modified.Month == month;
            }
        }
        label = "";
        return null;
    }

    private static bool IsKindWord(string token) => token is
        "video" or "movie" or "clip" or "film" or "photo" or "picture" or "image" or
        "screenshot" or "audio" or "song" or "music" or "recording" or "pdf" or
        "document" or "doc" or "voice";

    private static bool IsDateWord(string token) => token is
        "yesterday" or "today" or "tonight" or "morning" or "night" or "last" or
        "week" or "month" or "latest" or "newest" or "recent" or "this";

    private static string Normalize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value.ToLowerInvariant())
        {
            builder.Append(char.IsLetterOrDigit(character) ? character : ' ');
        }
        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static List<string> Tokens(string value) =>
        Normalize(value).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

    private static bool ContainsWord(string text, string word) =>
        Tokens(text).Contains(word, StringComparer.Ordinal);

    private static bool ContainsAny(string text, params string[] words) =>
        words.Any(word => ContainsWord(text, word));

    private static int LastIndexOfWord(string text, string word)
    {
        var index = -1;
        var search = 0;
        while (true)
        {
            var found = text.IndexOf(word, search, StringComparison.Ordinal);
            if (found < 0) return index;
            var beforeOk = found == 0 || text[found - 1] == ' ';
            var after = found + word.Length;
            var afterOk = after >= text.Length || text[after] == ' ';
            if (beforeOk && afterOk) index = found;
            search = found + word.Length;
        }
    }
}
