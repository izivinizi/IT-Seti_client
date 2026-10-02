using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Win32;

namespace ITSeti.Maintenance.Win7
{
    internal static class LegacyExtras
    {
        public static void Collect(LegacySnapshot snapshot)
        {
            CollectHardware(snapshot);
            CollectIdentity(snapshot);
            CollectNetwork(snapshot);
            CollectProcesses(snapshot);
        }

        private static void CollectHardware(LegacySnapshot snapshot)
        {
            try
            {
                using (var search = new ManagementObjectSearcher("root\\cimv2", "SELECT Name FROM Win32_Processor"))
                using (var values = search.Get())
                    snapshot.CpuName = values.Cast<ManagementObject>().Select(x => Convert.ToString(x["Name"])).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
            }
            catch (Exception ex) { snapshot.Unavailable.Add("Модель CPU: " + ex.Message); }
            try
            {
                using (var search = new ManagementObjectSearcher("root\\cimv2", "SELECT Name FROM Win32_VideoController"))
                using (var values = search.Get())
                {
                    var names = values.Cast<ManagementObject>().Select(x => Convert.ToString(x["Name"]))
                        .Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
                    snapshot.GpuName = names.FirstOrDefault(x => !Regex.IsMatch(x, "Virtual|Remote|Parsec|Basic Display|Mirror", RegexOptions.IgnoreCase))
                        ?? names.FirstOrDefault();
                }
            }
            catch (Exception ex) { snapshot.Unavailable.Add("Видеокарта: " + ex.Message); }
            try
            {
                using (var search = new ManagementObjectSearcher("root\\cimv2", "SELECT SMBIOSMemoryType FROM Win32_PhysicalMemory"))
                using (var values = search.Get())
                {
                    var kinds = values.Cast<ManagementObject>()
                        .Select(x => Convert.ToInt32(x["SMBIOSMemoryType"] ?? 0))
                        .Where(x => x != 0).Distinct().ToArray();
                    snapshot.MemoryType = string.Join(", ", kinds.Select(x => x == 20 ? "DDR" : x == 21 ? "DDR2" : x == 24 ? "DDR3" : x == 26 ? "DDR4" : x == 34 ? "DDR5" : "тип " + x));
                }
            }
            catch (Exception ex) { snapshot.Unavailable.Add("Тип памяти: " + ex.Message); }

            foreach (var scope in new[] { "root\\LibreHardwareMonitor", "root\\OpenHardwareMonitor" })
            {
                try
                {
                    using (var search = new ManagementObjectSearcher(scope, "SELECT Name,Value,SensorType FROM Sensor WHERE SensorType='Temperature'"))
                    using (var values = search.Get())
                    {
                        var temperatures = values.Cast<ManagementObject>()
                            .Where(x => Regex.IsMatch(Convert.ToString(x["Name"]), "CPU Package|CPU Core|Core Max|Tctl|Tdie", RegexOptions.IgnoreCase))
                            .Select(x => { double value; return double.TryParse(Convert.ToString(x["Value"]), out value) ? value : -1; })
                            .Where(x => x >= 5 && x <= 120).ToArray();
                        if (temperatures.Length == 0) continue;
                        snapshot.CpuTemperatureC = temperatures.Max();
                        if (snapshot.CpuTemperatureC >= 85) snapshot.Findings.Add("Критическая температура CPU: " + snapshot.CpuTemperatureC.Value.ToString("N0") + " °C.");
                        else if (snapshot.CpuTemperatureC >= 75) snapshot.Findings.Add("Высокая температура CPU: " + snapshot.CpuTemperatureC.Value.ToString("N0") + " °C.");
                        break;
                    }
                }
                catch (ManagementException) { }
                catch (UnauthorizedAccessException) { }
            }
            if (!snapshot.CpuTemperatureC.HasValue)
                snapshot.Unavailable.Add("Температура CPU: совместимый датчик не найден; значение не подменяется температурой платы.");
        }

        private static void CollectIdentity(LegacySnapshot snapshot)
        {
            var inventory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ITSeti", "MaintenanceWin7", "inventory.txt");
            var sharedInventory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "ITSeti", "Maintenance", "inventory.txt");
            try
            {
                if (!File.Exists(inventory)) inventory = sharedInventory;
                if (File.Exists(inventory))
                {
                    var value = File.ReadAllText(inventory).Trim();
                    if (Regex.IsMatch(value, @"^\d{4}$")) snapshot.InventoryNumber = value;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            foreach (var path in new[] { @"SOFTWARE\TektonIT\RMS Host\Host\Parameters", @"SOFTWARE\WOW6432Node\TektonIT\RMS Host\Host\Parameters" })
            {
                try
                {
                    using (var key = Registry.LocalMachine.OpenSubKey(path))
                    {
                        var value = key == null ? null : key.GetValue("InternetId");
                        var content = value is byte[] ? Encoding.UTF8.GetString((byte[])value).TrimEnd('\0') : value as string;
                        if (string.IsNullOrWhiteSpace(content)) continue;
                        var id = XDocument.Parse(content.TrimStart('\uFEFF')).Descendants()
                            .FirstOrDefault(x => x.Name.LocalName == "internet_id");
                        if (id != null && id.Value.Length <= 64) { snapshot.RmsId = id.Value.Trim(); break; }
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Xml.XmlException) { }
            }

            foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) })
            {
                try
                {
                    foreach (var directory in Directory.GetDirectories(root, "AnyDesk*"))
                    {
                        var file = Path.Combine(directory, "system.conf");
                        if (!File.Exists(file)) continue;
                        var id = Regex.Match(File.ReadAllText(file), @"(?m)^\s*ad\.anynet\.id=(\d{6,20})\s*$", RegexOptions.IgnoreCase);
                        if (id.Success) { snapshot.AnyDeskId = id.Groups[1].Value; return; }
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private static void CollectNetwork(LegacySnapshot snapshot)
        {
            try
            {
                foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    var kind = adapter.NetworkInterfaceType;
                    if (kind != NetworkInterfaceType.Ethernet && kind != NetworkInterfaceType.GigabitEthernet &&
                        kind != NetworkInterfaceType.FastEthernetT && kind != NetworkInterfaceType.Wireless80211 &&
                        kind != NetworkInterfaceType.Ppp && kind != NetworkInterfaceType.Tunnel) continue;
                    var addresses = adapter.GetIPProperties().UnicastAddresses
                        .Select(x => x.Address).Where(x => !System.Net.IPAddress.IsLoopback(x) &&
                            !(x.AddressFamily == AddressFamily.InterNetworkV6 && x.IsIPv6LinkLocal)).Select(x => x.ToString()).ToArray();
                    if (addresses.Length == 0) continue;
                    snapshot.Network.Add(new LegacyNetworkAdapter { Name = adapter.Name,
                        Kind = kind == NetworkInterfaceType.Wireless80211 ? "Wi-Fi" : kind == NetworkInterfaceType.Ppp || kind == NetworkInterfaceType.Tunnel ? "VPN" : "Ethernet",
                        Status = "Подключено", Speed = adapter.Speed > 0 ? (adapter.Speed / 1000000) + " Мбит/с" : "неизвестно",
                        Addresses = string.Join(", ", addresses) });
                }
            }
            catch (Exception ex) { snapshot.Unavailable.Add("Сеть: " + ex.Message); }
        }

        private static void CollectProcesses(LegacySnapshot snapshot)
        {
            try
            {
                foreach (var process in Process.GetProcesses())
                {
                    using (process)
                    {
                        try
                        {
                            snapshot.Processes.Add(new LegacyProcess { Name = process.ProcessName,
                                MemoryBytes = process.WorkingSet64, Path = ReadProcessPath(process) });
                        }
                        catch (InvalidOperationException) { }
                        catch (System.ComponentModel.Win32Exception) { }
                    }
                }
                snapshot.Processes = snapshot.Processes.OrderByDescending(x => x.MemoryBytes).Take(80).ToList();
            }
            catch (Exception ex) { snapshot.Unavailable.Add("Процессы: " + ex.Message); }
        }

        private static string ReadProcessPath(Process process)
        {
            try { return process.MainModule.FileName; }
            catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception) { return "нет доступа"; }
        }
    }
}
