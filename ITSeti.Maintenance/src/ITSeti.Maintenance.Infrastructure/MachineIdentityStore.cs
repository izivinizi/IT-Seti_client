using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;
using Microsoft.Win32;

namespace ITSeti.Maintenance.Infrastructure;

public sealed record MachineIdentity(string? InventoryNumber, string? RmsId, IReadOnlyList<string> IpAddresses);

public sealed class MachineIdentityStore(string? inventoryPath = null)
{
    public string InventoryPath { get; } = inventoryPath ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ITSeti", "Maintenance", "inventory.txt");

    public MachineIdentity Read()
    {
        string? inventory = null;
        try
        {
            if (File.Exists(InventoryPath))
            {
                var value = File.ReadAllText(InventoryPath).Trim();
                if (IsValidInventoryNumber(value)) inventory = value;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return new MachineIdentity(inventory, ReadRmsId(), ReadIpAddresses());
    }

    public void SaveInventoryNumber(string value)
    {
        if (!IsValidInventoryNumber(value)) throw new ArgumentException("Введите ровно четыре цифры инвентарного номера.");
        Directory.CreateDirectory(Path.GetDirectoryName(InventoryPath)!);
        File.WriteAllText(InventoryPath, value, Encoding.ASCII);
    }

    public static bool IsValidInventoryNumber(string? value) => value is { Length: 4 } && value.All(c => c is >= '0' and <= '9');

    private static string? ReadRmsId()
    {
        foreach (var path in new[]
        {
            @"SOFTWARE\TektonIT\RMS Host\Host\Parameters",
            @"SOFTWARE\WOW6432Node\TektonIT\RMS Host\Host\Parameters"
        })
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(path);
                var value = key?.GetValue("InternetId");
                var xml = value switch
                {
                    byte[] bytes => Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF').TrimEnd('\0'),
                    string text => text,
                    _ => null
                };
                if (string.IsNullOrWhiteSpace(xml)) continue;
                xml = xml.TrimStart('\uFEFF');
                var id = ParseRmsInternetId(xml);
                if (id is not null) return id;
            }
            catch (Exception ex) when (ex is ArgumentException or System.Xml.XmlException or IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    public static string? ParseRmsInternetId(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return null;
        try
        {
            var id = XDocument.Parse(xml.TrimStart('\uFEFF')).Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "internet_id")?.Value.Trim();
            return id is { Length: > 0 and <= 64 } && !id.Any(char.IsControl) ? id : null;
        }
        catch (System.Xml.XmlException) { return null; }
    }

    private static IReadOnlyList<string> ReadIpAddresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a)
                    && !a.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                .Select(a => a.ToString()).Distinct().Order(StringComparer.Ordinal).ToArray();
        }
        catch (NetworkInformationException) { return []; }
    }
}
