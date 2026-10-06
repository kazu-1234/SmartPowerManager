using System.Net.NetworkInformation;

namespace SmartPowerManager.Services;

public sealed class MacAddressInfo
{
    public required string Name { get; init; }
    public required string Mac { get; init; }
}

public static class MacAddressService
{
    private static readonly string[] VpnKeywords =
    [
        "VPN", "TAP", "Wintun", "WireGuard", "OpenVPN", "Nord",
        "Hyper-V", "vEthernet", "VirtualBox", "VMware", "Tailscale"
    ];

    /// <summary>ローカル NIC の MAC を返す（VPN/Tunnel 除外・重複除去・Up 優先）。</summary>
    public static IReadOnlyList<MacAddressInfo> GetMacAddresses()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var up = new List<MacAddressInfo>();
        var down = new List<MacAddressInfo>();

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ShouldSkip(nic))
                    continue;

                byte[] bytes = nic.GetPhysicalAddress().GetAddressBytes();
                if (bytes.Length != 6)
                    continue;
                if (bytes.All(b => b == 0))
                    continue;

                string mac = string.Join(":", bytes.Select(b => b.ToString("X2")));
                if (!seen.Add(mac))
                    continue;

                string name = nic.Name;
                if (string.IsNullOrWhiteSpace(name))
                    name = nic.Description;
                if (string.IsNullOrWhiteSpace(name))
                    name = "NIC";

                var info = new MacAddressInfo { Name = name, Mac = mac };
                if (nic.OperationalStatus == OperationalStatus.Up)
                    up.Add(info);
                else
                    down.Add(info);
            }
        }
        catch
        {
            return [];
        }

        up.AddRange(down);
        return up;
    }

    private static bool ShouldSkip(NetworkInterface nic)
    {
        if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback
            or NetworkInterfaceType.Ppp
            or NetworkInterfaceType.Tunnel)
            return true;

        string haystack = $"{nic.Name} {nic.Description}";
        foreach (string kw in VpnKeywords)
        {
            if (haystack.Contains(kw, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
