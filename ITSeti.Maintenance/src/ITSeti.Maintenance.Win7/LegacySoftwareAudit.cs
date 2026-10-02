using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Win32;

namespace ITSeti.Maintenance.Win7
{
    internal sealed class LegacySoftwareEntry
    {
        public string Name { get; set; }
        public string Version { get; set; }
        public string State { get; set; }
    }

    internal static class LegacySoftwareAudit
    {
        private static readonly string[] Expected =
        {
            "AnyDesk", "RMS Host", "OCS Inventory", "Desktop Info", "WinRAR", "Яндекс Браузер"
        };

        public static List<LegacySoftwareEntry> Collect()
        {
            var installed = new List<LegacySoftwareEntry>();
            foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
            {
                try
                {
                    using (var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                    using (var uninstall = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"))
                    {
                        if (uninstall == null) continue;
                        foreach (var keyName in uninstall.GetSubKeyNames())
                        {
                            try
                            {
                                using (var key = uninstall.OpenSubKey(keyName))
                                {
                                    var name = Convert.ToString(key == null ? null : key.GetValue("DisplayName"));
                                    if (string.IsNullOrWhiteSpace(name)) continue;
                                    installed.Add(new LegacySoftwareEntry { Name = name.Trim(),
                                        Version = Convert.ToString(key.GetValue("DisplayVersion")) ?? "", State = "Установлено" });
                                }
                            }
                            catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException) { }
                        }
                    }
                }
                catch (Exception ex) when (ex is ArgumentException || ex is System.IO.IOException || ex is UnauthorizedAccessException) { }
            }
            var result = new List<LegacySoftwareEntry>();
            foreach (var expected in Expected)
            {
                var needle = expected == "Яндекс Браузер" ? "Yandex" : expected;
                var match = installed.FirstOrDefault(x => x.Name.IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    x.Name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0);
                result.Add(match == null
                    ? new LegacySoftwareEntry { Name = expected, Version = "—", State = "Не найдено в списке программ" }
                    : new LegacySoftwareEntry { Name = expected, Version = match.Version, State = "Установлено" });
            }
            return result;
        }
    }
}
