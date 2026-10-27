using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Android.Content;
using Android.Net;
using AndroidApplication = Android.App.Application;

namespace Wisp.Android;

/// <summary>
/// Resolves the IPv4 address the phone has on its LocalOnlyHotspot. Android has
/// no public API that returns the soft-AP address, so this combines the
/// LinkProperties of Wi-Fi networks (the documented connectivity view) with a
/// NetworkInterface scan that finds OEM hotspot interfaces such as ap0,
/// swlan0, wlan1 or softap0. The interface scan is preferred because a device
/// with concurrent station + hotspot connections may still report the home
/// Wi-Fi network in ConnectivityManager.
/// </summary>
internal static class HotspotAddressResolver
{
    public static string? Resolve(Context context)
    {
        var fromInterface = ResolveFromInterfaces();
        if (fromInterface is not null) return fromInterface;
        return ResolveFromLinkProperties(context);
    }

    private static string? ResolveFromInterfaces()
    {
        string? wifiCandidate = null;
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var name = adapter.Name.ToLowerInvariant();
                var hotspotInterface = name.StartsWith("ap", StringComparison.Ordinal) ||
                                       name.StartsWith("swlan", StringComparison.Ordinal) ||
                                       name.StartsWith("softap", StringComparison.Ordinal) ||
                                       name.Contains("softap") ||
                                       name.StartsWith("wlan1", StringComparison.Ordinal) ||
                                       name.Contains("p2p");
                var wifiInterface = hotspotInterface || name.StartsWith("wlan", StringComparison.Ordinal);
                IPAddress? address;
                try
                {
                    address = adapter.GetIPProperties().UnicastAddresses
                        .Select(info => info.Address)
                        .FirstOrDefault(candidate => IsUsableIpv4(candidate));
                }
                catch
                {
                    continue;
                }
                if (address is null) continue;
                var text = address.ToString();
                if (hotspotInterface) return text;
                if (wifiInterface && IsPrivate(address)) wifiCandidate ??= text;
            }
        }
        catch
        {
        }
        return wifiCandidate;
    }

    private static string? ResolveFromLinkProperties(Context context)
    {
        try
        {
            if (context.GetSystemService(Context.ConnectivityService) is not ConnectivityManager connectivity)
                return null;
#pragma warning disable CA1422 // Required for LinkProperties of every Wi-Fi network.
            foreach (var network in connectivity.GetAllNetworks())
            {
                var capabilities = connectivity.GetNetworkCapabilities(network);
                if (capabilities?.HasTransport(TransportType.Wifi) != true) continue;
                var properties = connectivity.GetLinkProperties(network);
                if (properties?.LinkAddresses is null) continue;
                foreach (var link in properties.LinkAddresses)
                {
                    if (link.Address is not Java.Net.Inet4Address ipv4) continue;
                    if (IPAddress.TryParse(ipv4.HostAddress, out var parsed) && IsUsableIpv4(parsed))
                        return parsed.ToString();
                }
            }
#pragma warning restore CA1422
        }
        catch
        {
        }
        return null;
    }

    private static bool IsUsableIpv4(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        if (IPAddress.IsLoopback(address)) return false;
        var bytes = address.GetAddressBytes();
        if (bytes[0] == 169 && bytes[1] == 254) return false;
        return bytes[0] != 0;
    }

    private static bool IsPrivate(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 ||
               (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
               (bytes[0] == 192 && bytes[1] == 168);
    }

    /// <summary>Typical AOSP soft-AP gateway, used only when detection fails.</summary>
    public static string CommonFallbackAddress => "192.168.43.1";
}
