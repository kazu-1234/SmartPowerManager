using System.Net.NetworkInformation;

namespace SmartPowerManager.Services;

public sealed class MacAddressInfo
{
    public required string Name { get; init; }
    public required string Mac { get; init; }
}

public static class MacAddressService
{
    /// <summary>ローカル NIC の MAC を AA:BB:CC:DD:EE:FF 形式で返す（重複除去・Up 優先）。</summary>
    public static IReadOnlyList<MacAddressInfo> GetMacAddresses()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var up = new List<MacAddressInfo>();
        var down = new List<MacAddressInfo>();

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;

                byte[] bytes = nic.GetPhysicalAddress().GetAddressBytes();
                if (bytes.Length != 6)
                    continue;
                if (bytes.All(b => b == 0))
                    continue;

                string mac = string.Join(":", bytes.Select(b => b.ToString("X2")));
                if (!seen.Add(mac))
                    continue;

                var info = new MacAddressInfo { Name = nic.Name, Mac = mac };
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
}
