using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wisp.Core;

public sealed record UpdateManifest
{
    public string Latest { get; init; } = "";
    public string Name { get; init; } = "";
    public string Published { get; init; } = "";
    public string Notes { get; init; } = "";
    [JsonPropertyName("release_url")] public string ReleaseUrl { get; init; } = "";
    [JsonPropertyName("android_url")] public string AndroidUrl { get; init; } = "";
    [JsonPropertyName("windows_url")] public string WindowsUrl { get; init; } = "";
    public bool Mandatory { get; init; }
}

public sealed record UpdateCheckResult(
    UpdateManifest? Manifest,
    bool IsNewer,
    string? Error);

/// <summary>
/// Checks a static JSON manifest hosted with the repository. This avoids a
/// custom update server and GitHub API rate limits. The request carries no
/// identifiers beyond the normal HTTPS request and the app version.
/// </summary>
public static class UpdateService
{
    public const string DefaultManifestUrl =
        "https://raw.githubusercontent.com/sudaissalisu/wisp/main/update.json";

    public static string ManifestUrl
    {
        get
        {
            var custom = Environment.GetEnvironmentVariable("WISP_UPDATE_MANIFEST_URL");
            return string.IsNullOrWhiteSpace(custom) ? DefaultManifestUrl : custom;
        }
    }

    public static async Task<UpdateCheckResult> CheckAsync(string currentVersion,
        string platform, CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                $"Wisp/{currentVersion} ({platform})");
            var separator = ManifestUrl.Contains('?') ? '&' : '?';
            var url = $"{ManifestUrl}{separator}v={Uri.EscapeDataString(currentVersion)}" +
                      $"&t={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
            using var response = await client.GetAsync(url, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new UpdateCheckResult(null, false,
                    $"The update manifest returned HTTP {(int)response.StatusCode}.");
            }
            var json = await response.Content.ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);
            var manifest = ParseManifest(json);
            if (manifest is null || string.IsNullOrWhiteSpace(manifest.Latest))
            {
                return new UpdateCheckResult(null, false, "The update manifest is invalid.");
            }
            return new UpdateCheckResult(manifest,
                IsNewer(manifest.Latest, currentVersion), null);
        }
        catch (OperationCanceledException)
        {
            return new UpdateCheckResult(null, false, "The update check timed out.");
        }
        catch (Exception exception)
        {
            return new UpdateCheckResult(null, false,
                $"Could not check for updates: {exception.Message}");
        }
    }

    public static UpdateManifest? ParseManifest(string json)
    {
        try
        {
            var manifest = JsonSerializer.Deserialize<UpdateManifest>(json,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (manifest is null) return null;
            return manifest with
            {
                Latest = manifest.Latest.Trim(),
                Name = string.IsNullOrWhiteSpace(manifest.Name)
                    ? $"Wisp {manifest.Latest.Trim()}"
                    : manifest.Name.Trim(),
                Notes = TrimNotes(manifest.Notes),
                ReleaseUrl = manifest.ReleaseUrl.Trim(),
                AndroidUrl = manifest.AndroidUrl.Trim(),
                WindowsUrl = manifest.WindowsUrl.Trim(),
                Published = manifest.Published.Trim()
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string? DownloadUrl(UpdateManifest manifest, string platform)
    {
        var url = platform.Equals("android", StringComparison.OrdinalIgnoreCase)
            ? manifest.AndroidUrl
            : manifest.WindowsUrl;
        if (string.IsNullOrWhiteSpace(url)) url = manifest.ReleaseUrl;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)) return null;
        return parsed.Scheme == Uri.UriSchemeHttps ? url : null;
    }

    public static bool IsNewer(string latest, string current)
    {
        var latestVersion = ParseVersion(latest);
        var currentVersion = ParseVersion(current);
        return latestVersion is not null && currentVersion is not null &&
               latestVersion > currentVersion;
    }

    private static Version? ParseVersion(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim().TrimStart('v', 'V');
        var cutoff = value.IndexOfAny(['-', '+']);
        if (cutoff >= 0) value = value[..cutoff];
        var pieces = value.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (pieces.Length is < 2 or > 4) return null;
        foreach (var piece in pieces)
        {
            if (!int.TryParse(piece, out _)) return null;
        }
        return Version.TryParse(value, out var parsed) ? parsed : null;
    }

    private static string TrimNotes(string notes)
    {
        var value = (notes ?? "").Replace("\r\n", "\n").Trim();
        return value.Length <= 6000 ? value : value[..6000] + "\n…";
    }
}
