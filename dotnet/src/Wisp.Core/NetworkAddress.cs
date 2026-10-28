using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Wisp.Core;

/// <summary>Finds the LAN address a browser on the same Wi-Fi can reach.</summary>
public static class NetworkAddress
{
    public static string? FindLanIPv4()
    {
        try
        {
            string? best = null;
            var bestScore = -1;
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up) continue;
                if (adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or
                    NetworkInterfaceType.Tunnel) continue;
                var name = adapter.Name.ToLowerInvariant();
                if (name.StartsWith("lo") || name.StartsWith("tun") || name.StartsWith("tap") ||
                    name.StartsWith("rmnet") || name.StartsWith("ccmni") ||
                    name.StartsWith("dummy")) continue;

                var score = adapter.NetworkInterfaceType switch
                {
                    NetworkInterfaceType.Wireless80211 => 100,
                    NetworkInterfaceType.Ethernet => 80,
                    NetworkInterfaceType.GigabitEthernet => 85,
                    NetworkInterfaceType.Unknown => 60,
                    _ => 20
                };
                if (name.StartsWith("ap") || name.StartsWith("swlan") ||
                    name.StartsWith("softap") || name.StartsWith("wlan1"))
                {
                    score += 10;
                }

                foreach (var unicast in adapter.GetIPProperties().UnicastAddresses)
                {
                    var address = unicast.Address;
                    if (address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(address)) continue;
                    if (!IsPrivate(address)) continue;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = address.ToString();
                    }
                }
            }
            return best;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsPrivate(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        if (bytes.Length != 4) return false;
        return bytes[0] == 10 ||
               (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
               (bytes[0] == 192 && bytes[1] == 168) ||
               (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127);
    }
}
