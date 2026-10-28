using Android.Content;
using Android.Net.Wifi;
using Wisp.Core;
using AndroidApplication = Android.App.Application;

namespace Wisp.Android;

/// <summary>
/// Prefers the portable interface scan, then falls back to WifiManager's IPv4
/// value for Android builds where NetworkInterface does not expose wlan0.
/// </summary>
internal static class LanAddress
{
    public static string? Find()
    {
        var address = NetworkAddress.FindLanIPv4();
        if (!string.IsNullOrWhiteSpace(address)) return address;
        try
        {
            var wifi = (WifiManager?)AndroidApplication.Context.GetSystemService(
                Context.WifiService);
            var info = wifi?.ConnectionInfo;
            var ip = info?.IpAddress ?? 0;
            if (ip == 0) return null;
            var bytes = new[]
            {
                (byte)(ip & 0xff),
                (byte)((ip >> 8) & 0xff),
                (byte)((ip >> 16) & 0xff),
                (byte)((ip >> 24) & 0xff)
            };
            if (bytes[0] is 0 or 127) return null;
            return $"{bytes[0]}.{bytes[1]}.{bytes[2]}.{bytes[3]}";
        }
        catch
        {
            return null;
        }
    }
}
