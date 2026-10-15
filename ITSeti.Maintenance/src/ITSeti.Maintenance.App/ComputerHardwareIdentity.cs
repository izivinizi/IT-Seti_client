using System.Management;
using System.Security;

namespace ITSeti.Maintenance.App;

internal sealed record ComputerHardwareIdentity(string? Gpu, string? MemoryType)
{
    public static ComputerHardwareIdentity Read()
    {
        var gpu = ReadNames("Win32_VideoController", row =>
        {
            var name = row["Name"]?.ToString()?.Trim();
            var id = row["PNPDeviceID"]?.ToString() ?? "";
            return id.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase) ? name : null;
        });
        var memory = ReadNames("Win32_PhysicalMemory", row =>
        {
            var type = Convert.ToInt32(row["SMBIOSMemoryType"] ?? 0);
            if (type <= 1) type = Convert.ToInt32(row["MemoryType"] ?? 0);
            return type switch
            {
                20 => "DDR", 21 => "DDR2", 24 => "DDR3", 26 => "DDR4", 34 => "DDR5",
                27 => "LPDDR", 28 => "LPDDR2", 29 => "LPDDR3", 30 => "LPDDR4", 35 => "LPDDR5",
                18 => "SGRAM", 17 => "SDRAM", _ => null
            };
        });
        return new(gpu, memory);
    }

    private static string? ReadNames(string className, Func<ManagementBaseObject, string?> select)
    {
        try
        {
            using var search = new ManagementObjectSearcher("root\\cimv2", "SELECT * FROM " + className);
            search.Options.Timeout = TimeSpan.FromSeconds(5);
            using var rows = search.Get();
            var values = new List<string>();
            foreach (ManagementBaseObject row in rows)
                using (row) if (select(row) is { Length: > 0 } name) values.Add(name);
            return values.Count == 0 ? null : string.Join(", ", values.Distinct(StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or SecurityException)
        { return null; }
    }
}
