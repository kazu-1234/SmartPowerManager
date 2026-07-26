using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text;

namespace SmartPowerManager.Services;

public sealed class MacAddressInfo
{
    public required string Name { get; init; }
    public required string Mac { get; init; }
}

public static class MacAddressService
{
    public static IReadOnlyList<MacAddressInfo> GetMacAddresses()
    {
        var list = new List<MacAddressInfo>();

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "getmac",
                Arguments = "/v /fo csv",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                StandardOutputEncoding = Encoding.GetEncoding(932)
            });

            if (process == null)
                return Fallback();

            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            bool headerSkipped = false;
            foreach (string line in lines)
            {
                if (!headerSkipped)
                {
                    headerSkipped = true;
                    continue;
                }

                var parts = ParseCsvLine(line);
                if (parts.Count < 3)
                    continue;

                string adapterName = parts[1].Trim();
                string mac = parts[2].Trim().Replace('-', ':');
                if (string.IsNullOrWhiteSpace(mac) || mac.Equals("N/A", StringComparison.OrdinalIgnoreCase))
                    continue;

                list.Add(new MacAddressInfo { Name = adapterName, Mac = mac });
            }
        }
        catch
        {
            return Fallback();
        }

        return list.Count > 0 ? list : Fallback();
    }

    private static IReadOnlyList<MacAddressInfo> Fallback()
    {
        try
        {
            var nic = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n =>
                    n.OperationalStatus == OperationalStatus.Up &&
                    n.NetworkInterfaceType != NetworkInterfaceType.Loopback);

            if (nic == null)
                return [];

            string mac = string.Join(":", nic.GetPhysicalAddress().GetAddressBytes().Select(b => b.ToString("X2")));
            return [new MacAddressInfo { Name = nic.Name, Mac = mac }];
        }
        catch
        {
            return [];
        }
    }

    private static List<string> ParseCsvLine(string line)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;

        foreach (char c in line)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (c == ',' && !inQuotes)
            {
                result.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(c);
        }

        result.Add(current.ToString());
        return result;
    }
}
