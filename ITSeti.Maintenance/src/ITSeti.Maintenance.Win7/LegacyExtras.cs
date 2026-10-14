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
                var physical = new List<Tuple<int, string>>();
                var virtualAdapters = new List<string>();
                using (var search = new ManagementObjectSearcher("root\\cimv2", "SELECT Name, VideoProcessor, AdapterCompatibility, PNPDeviceID FROM Win32_VideoController"))
                using (var values = search.Get())
                {
                    foreach (ManagementObject adapter in values)
                        AddGpuCandidate(adapter, physical, virtualAdapters);
                }
                if (physical.Count == 0)
                {
                    using (var fallback = new ManagementObjectSearcher("root\\cimv2", "SELECT Name, PNPDeviceID FROM Win32_PnPEntity WHERE PNPClass='Display'"))
                    using (var fallbackValues = fallback.Get())
                    {
                        foreach (ManagementObject adapter in fallbackValues)
                            AddGpuCandidate(adapter, physical, virtualAdapters);
                    }
                }
                var names = physical.OrderBy(x => x.Item1).Select(x => x.Item2)
                    .Concat(virtualAdapters).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                snapshot.GpuName = names.Length == 0 ? "нет данных" : string.Join(", ", names);
                if (names.Length == 0) snapshot.Unavailable.Add("Видеокарта: WMI не вернул имя, видеопроцессор или совместимость адаптера.");
            }
            catch (Exception ex) { snapshot.Unavailable.Add("Видеокарта: " + ex.Message); }
            try
            {
                using (var search = new ManagementObjectSearcher("root\\cimv2", "SELECT MemoryType, SMBIOSMemoryType FROM Win32_PhysicalMemory"))
                using (var values = search.Get())
                {
                    var kinds = values.Cast<ManagementObject>()
                        .Select(x => Convert.ToInt32(x["SMBIOSMemoryType"] ?? 0) is var smbios && smbios != 0
                            ? smbios : Convert.ToInt32(x["MemoryType"] ?? 0))
                        .Where(x => x != 0).Distinct().ToArray();
                    snapshot.MemoryType = string.Join(", ", kinds.Select(FormatMemoryType));
                }
            }
            catch (Exception ex) { snapshot.Unavailable.Add("Тип памяти: " + ex.Message); }

            // Win7 uses the same bundled HWiNFO reader as the main client.
            // Do not query a second, legacy sensor provider here.
            if (!snapshot.CpuTemperatureC.HasValue)
                snapshot.Unavailable.Add("Температура CPU: служба HWiNFO не вернула допустимый датчик.");
        }

        private static void CollectIdentity(LegacySnapshot snapshot)
        {
            var localInventory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ITSeti", "MaintenanceWin7", "inventory.txt");
            var sharedInventory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "ITSeti", "Maintenance", "inventory.txt");
            foreach (var inventory in new[] { sharedInventory, localInventory })
            {
                try
                {
                    if (!File.Exists(inventory)) continue;
                    var value = File.ReadAllText(inventory).Trim();
                    if (Regex.IsMatch(value, @"^\d{4}$")) { snapshot.InventoryNumber = value; break; }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }

            if (IsRmsHostRunning()) foreach (var path in new[] { @"SOFTWARE\TektonIT\RMS Host\Host\Parameters", @"SOFTWARE\WOW6432Node\TektonIT\RMS Host\Host\Parameters" })
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

        private static string FormatMemoryType(int value)
        {
            switch (value)
            {
                case 0: return "неизвестный тип (0)";
                case 1: return "другой тип памяти";
                case 2: return "DRAM";
                case 3: return "Synchronous DRAM";
                case 4: return "Cache DRAM";
                case 5: return "EDO";
                case 6: return "EDRAM";
                case 7: return "VRAM";
                case 8: return "SRAM";
                case 9: return "RAM";
                case 10: return "ROM";
                case 11: return "Flash";
                case 12: return "EEPROM";
                case 13: return "FEPROM";
                case 14: return "EPROM";
                case 15: return "CDRAM";
                case 16: return "3DRAM";
                case 17: return "SDRAM";
                case 18: return "SGRAM";
                case 19: return "RDRAM";
                case 20: return "DDR";
                case 21: return "DDR2";
                case 22: return "BRAM";
                case 23: return "FB-DIMM";
                case 24: return "DDR3";
                case 25: return "FBD2";
                case 26: return "DDR4";
                case 27: return "LPDDR";
                case 28: return "LPDDR2";
                case 29: return "LPDDR3";
                case 30: return "LPDDR4";
                case 31: return "энергонезависимая память";
                case 32: return "HBM";
                case 33: return "HBM2";
                case 34: return "DDR5";
                case 35: return "LPDDR5";
                case 36: return "HBM3";
                default: return "тип " + value;
            }
        }

        private static void AddGpuCandidate(ManagementBaseObject adapter, ICollection<Tuple<int, string>> physical,
            ICollection<string> virtualAdapters)
        {
            var name = FirstValue(adapter, "Name", "VideoProcessor", "AdapterCompatibility");
            if (string.IsNullOrWhiteSpace(name)) return;
            var deviceId = Convert.ToString(adapter["PNPDeviceID"]) ?? string.Empty;
            var isPci = deviceId.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase);
            var looksVirtual = Regex.IsMatch(name, "Virtual|Remote|Parsec|Mirror|Indirect Display", RegexOptions.IgnoreCase) ||
                (!isPci && Regex.IsMatch(name, "Basic Display", RegexOptions.IgnoreCase));
            if (looksVirtual) virtualAdapters.Add(name.Trim());
            else physical.Add(Tuple.Create(isPci ? 0 : 1, name.Trim()));
        }

        private static string FirstValue(ManagementBaseObject item, params string[] properties)
        {
            foreach (var property in properties)
            {
                var value = Convert.ToString(item[property]);
                if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
            }
            return null;
        }

        private static bool IsRmsHostRunning()
        {
            var roots = new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) };
            if (!roots.Any(root => !string.IsNullOrWhiteSpace(root) &&
                File.Exists(Path.Combine(root, "Remote Manipulator System - Host", "rutserv.exe")))) return false;
            Process[] processes;
            try { processes = Process.GetProcessesByName("rutserv"); }
            catch { return false; }
            try { return processes.Any(process => { try { return !process.HasExited; } catch { return false; } }); }
            finally { foreach (var process in processes) process.Dispose(); }
        }

        internal static void CollectNetwork(LegacySnapshot snapshot)
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
                    var physicalAddress = adapter.GetPhysicalAddress().GetAddressBytes();
                    snapshot.Network.Add(new LegacyNetworkAdapter { Name = adapter.Name,
                        Kind = kind == NetworkInterfaceType.Wireless80211 ? "Wi-Fi" : kind == NetworkInterfaceType.Ppp || kind == NetworkInterfaceType.Tunnel ? "VPN" : "Ethernet",
                        Status = "Подключено", Speed = adapter.Speed > 0 ? (adapter.Speed / 1000000) + " Мбит/с" : "неизвестно",
                        MacAddress = physicalAddress.Length == 0 ? "" : BitConverter.ToString(physicalAddress).Replace('-', ':'),
                        Addresses = string.Join(", ", addresses) });
                }
            }
            catch (Exception ex) { snapshot.Unavailable.Add("Сеть: " + ex.Message); }
        }

        private static void CollectProcesses(LegacySnapshot snapshot)
        {
            try
            {
                var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var publishers = new List<string>();
                try
                {
                    var publisherFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"allowed-publishers.txt");
                    if (File.Exists(publisherFile)) publishers.AddRange(File.ReadAllLines(publisherFile).Where(x => !string.IsNullOrWhiteSpace(x) && !x.StartsWith("#")));
                    var policyPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ITSeti", "Maintenance", "process-policy.json");
                    if (!File.Exists(policyPath)) policyPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "allowed-processes.json");
                    if (File.Exists(policyPath))
                    {
                        if (new FileInfo(policyPath).Length > 2 * 1024 * 1024) throw new InvalidDataException("Process policy exceeds 2 MB.");
                        var policy = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(policyPath));
                        if (policy != null && policy.TryGetValue("allowedPublishers",out var signerValues) && signerValues is System.Collections.IEnumerable signerEntries)
                        {
                            publishers.Clear();
                            foreach (var entry in signerEntries) publishers.Add(Convert.ToString(entry));
                        }
                        if (policy != null && policy.TryGetValue("allowedNames", out var names) && names is System.Collections.IEnumerable entries)
                            foreach (var entry in entries)
                            {
                                var name = Convert.ToString(entry).Trim();
                                allowed.Add(name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name.Substring(0,name.Length-4) : name);
                            }
                    }
                }
                catch { }
                var signatures = new Dictionary<string,Tuple<string,string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var process in Process.GetProcesses())
                {
                    using (process)
                    {
                        try
                        {
                            if (allowed.Contains(process.ProcessName)) continue;
                            var path = ReadProcessPath(process);
                            if (!signatures.TryGetValue(path,out var signature))
                            {
                                LegacyProcessTrust.Inspect(path,out var status,out var signer);
                                signature = Tuple.Create(status,signer);
                                signatures[path] = signature;
                            }
                            string Normalize(string value) => new string(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
                            var normalizedSigner = Normalize(signature.Item2);
                            if (signature.Item1 == "Valid" && publishers.Any(x => Normalize(x).Length > 0 &&
                                (x.Trim().EndsWith("*") ? normalizedSigner.StartsWith(Normalize(x),StringComparison.Ordinal) : normalizedSigner == Normalize(x)))) continue;
                            snapshot.Processes.Add(new LegacyProcess { Name = process.ProcessName,
                                MemoryBytes = process.WorkingSet64, Path = path, Signature=signature.Item1, Signer=signature.Item2 });
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
