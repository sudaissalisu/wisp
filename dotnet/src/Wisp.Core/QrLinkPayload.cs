using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Wisp.Core;

/// <summary>
/// The data carried by a Wisp temporary-link QR code. The host starts a
/// LocalOnlyHotspot, then encodes its credentials, pinned TLS fingerprint,
/// listener endpoint and a one-time token. A second phone joins that hotspot
/// and adds the host to the existing native engine as a normal pinned peer.
/// </summary>
public sealed record QrLinkPayload
{
    public const int CurrentVersion = 1;
    public const string Scheme = "wisp";
    public const string UriHost = "connect";

    public required int Version { get; init; }
    public required string Ssid { get; init; }
    public required string Passphrase { get; init; }
    public required string Host { get; init; }
    public required int Port { get; init; }
    public required string Fingerprint { get; init; }
    public required string Token { get; init; }
    public required string DeviceName { get; init; }
    public required long ExpiresAtUnix { get; init; }

    public DateTimeOffset ExpiresAt => DateTimeOffset.FromUnixTimeSeconds(ExpiresAtUnix);

    public bool IsExpired => DateTimeOffset.UtcNow.ToUnixTimeSeconds() > ExpiresAtUnix;

    public string EndpointText => $"{Host}:{Port}";

    public static QrLinkPayload Create(
        string ssid,
        string passphrase,
        string host,
        int port,
        string fingerprint,
        string token,
        string deviceName,
        TimeSpan lifetime)
    {
        var expires = DateTimeOffset.UtcNow.Add(lifetime).ToUnixTimeSeconds();
        return new QrLinkPayload
        {
            Version = CurrentVersion,
            Ssid = ssid,
            Passphrase = passphrase,
            Host = host,
            Port = port,
            Fingerprint = fingerprint.Trim().ToLowerInvariant(),
            Token = token.Trim().ToLowerInvariant(),
            DeviceName = string.IsNullOrWhiteSpace(deviceName) ? "Nearby device" : deviceName.Trim(),
            ExpiresAtUnix = expires
        };
    }

    public string ToUriString()
    {
        var query = string.Join("&",
            $"v={CurrentVersion.ToString(CultureInfo.InvariantCulture)}",
            $"ssid={Uri.EscapeDataString(Ssid)}",
            $"pass={Uri.EscapeDataString(Passphrase)}",
            $"host={Uri.EscapeDataString(Host)}",
            $"port={Port.ToString(CultureInfo.InvariantCulture)}",
            $"fp={Uri.EscapeDataString(Fingerprint)}",
            $"token={Uri.EscapeDataString(Token)}",
            $"name={Uri.EscapeDataString(DeviceName)}",
            $"exp={ExpiresAtUnix.ToString(CultureInfo.InvariantCulture)}");
        return $"{Scheme}://{UriHost}?{query}";
    }

    /// <summary>Validates a payload before it is trusted or displayed.</summary>
    public bool TryValidate(out string error)
    {
        if (Version != CurrentVersion)
        {
            error = $"This QR code uses an unsupported Wisp link version ({Version}).";
            return false;
        }
        if (string.IsNullOrWhiteSpace(Ssid) || Ssid.Length > 64)
        {
            error = "The QR code has an invalid Wi-Fi name.";
            return false;
        }
        if (Passphrase.Length is < 8 or > 63)
        {
            error = "The QR code has an invalid Wi-Fi password.";
            return false;
        }
        if (!IPAddress.TryParse(Host, out var address) ||
            address.AddressFamily != AddressFamily.InterNetwork ||
            IPAddress.IsLoopback(address))
        {
            error = "The QR code has an invalid host address.";
            return false;
        }
        if (Port is <= 0 or > 65535)
        {
            error = "The QR code has an invalid port.";
            return false;
        }
        if (Fingerprint.Length != 64 || !IsHex(Fingerprint))
        {
            error = "The QR code has an invalid TLS fingerprint.";
            return false;
        }
        if (Token.Length is < 32 or > 128 || !IsHex(Token))
        {
            error = "The QR code has an invalid one-time token.";
            return false;
        }
        if (ExpiresAtUnix <= 0)
        {
            error = "The QR code has no expiry.";
            return false;
        }
        if (IsExpired)
        {
            error = "This connection code has expired. Create a new link on the other phone.";
            return false;
        }
        error = "";
        return true;
    }

    public static bool TryParse(string? text, out QrLinkPayload? payload, out string error)
    {
        payload = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "No QR code was found.";
            return false;
        }
        text = text.Trim();
        if (text.Length > 4096)
        {
            error = "That QR code is too large to be a Wisp link.";
            return false;
        }
        if (!text.StartsWith($"{Scheme}://", StringComparison.OrdinalIgnoreCase))
        {
            error = "That is not a Wisp connection code.";
            return false;
        }

        Uri uri;
        try
        {
            uri = new Uri(text, UriKind.Absolute);
        }
        catch (UriFormatException)
        {
            error = "That Wisp connection code is malformed.";
            return false;
        }
        if (!uri.Scheme.Equals(Scheme, StringComparison.OrdinalIgnoreCase) ||
            !uri.Host.Equals(UriHost, StringComparison.OrdinalIgnoreCase))
        {
            error = "That is not a Wisp connection code.";
            return false;
        }

        Dictionary<string, string> values;
        try
        {
            values = ParseQuery(uri.Query);
        }
        catch (UriFormatException)
        {
            error = "That Wisp connection code has malformed escaping.";
            return false;
        }
        string Get(string key) => values.TryGetValue(key, out var value) ? value : "";

        if (!int.TryParse(Get("v"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var version) ||
            !int.TryParse(Get("port"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) ||
            !long.TryParse(Get("exp"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var expires))
        {
            error = "That Wisp connection code is missing required fields.";
            return false;
        }

        var name = Get("name");
        var parsed = new QrLinkPayload
        {
            Version = version,
            Ssid = Get("ssid"),
            Passphrase = Get("pass"),
            Host = Get("host"),
            Port = port,
            Fingerprint = Get("fp").ToLowerInvariant(),
            Token = Get("token").ToLowerInvariant(),
            DeviceName = string.IsNullOrWhiteSpace(name) ? "Nearby device" : name,
            ExpiresAtUnix = expires
        };
        if (!parsed.TryValidate(out error)) return false;
        payload = parsed;
        return true;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0) continue;
            var key = Uri.UnescapeDataString(part[..separator]);
            var value = Uri.UnescapeDataString(part[(separator + 1)..]);
            values[key] = value;
        }
        return values;
    }

    private static bool IsHex(string value)
    {
        foreach (var character in value)
        {
            if (!char.IsAsciiHexDigit(character)) return false;
        }
        return true;
    }
}
