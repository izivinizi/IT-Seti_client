using System;
using System.Collections.Generic;
using System.Linq;
using System.Web.Script.Serialization;

namespace ITSeti.Maintenance.Win7
{
    public sealed partial class LegacySnapshot
    {
        [ScriptIgnore]
        public double? MemoryUsedPercent { get { return TotalMemoryGb > 0 ? (TotalMemoryGb - FreeMemoryGb) / TotalMemoryGb * 100 : (double?)null; } }
        public string ToJson()
        {
            var serializer = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };
            var report = serializer.Deserialize<Dictionary<string, object>>(serializer.Serialize(this));
            report["MemoryUsedPercent"] = MemoryUsedPercent;
            return serializer.Serialize(report);
        }

        public string ToReport()
        {
            var lines = new List<string>
            {
                "ИТ-Сети | Диагностика Windows 7 (beta)",
                "Компьютер: " + ComputerName,
                "Проверено: " + CheckedAt,
                "Тип: " + (Kind ?? "Быстрая"),
                "Система: " + OsName + " (" + OsVersion + ")",
                "Серийный номер: " + (string.IsNullOrWhiteSpace(SerialNumber) ? "не получен" : SerialNumber),
                "Инв. №: " + (InventoryNumber ?? "не указан") + " | RMS: " + (RmsId ?? "не найден") + " | AnyDesk: " + (AnyDeskId ?? "не найден"),
                "Локальные администраторы: " + (AdminAccounts.Count == 0 ? "не получены" : string.Join(", ", AdminAccounts)),
                "Последний запуск: " + (LastBootAt ?? "нет данных"),
                "Наработка после запуска: " + (ActiveUptimeHours.HasValue ? ActiveUptimeHours.Value.ToString("N0") + " ч" : "нет данных"),
                "CPU: " + (CpuName ?? "не определён") + " · " + (CpuPercent.HasValue && CpuPercent.Value > 0 ? (CpuPercent.Value < 1 ? "<1%" : CpuPercent.Value.ToString("N0") + "%") : "нет данных") +
                    " · источник: " + (CpuSource ?? "старый отчёт") + " · " + (CpuTemperatureC.HasValue ? CpuTemperatureC.Value.ToString("N0") + " °C" : "температура недоступна"),
                "GPU: " + (GpuName ?? "не определена") + " | Память: " + (MemoryType ?? "тип не определён"),
                "ОЗУ: " + (TotalMemoryGb.HasValue ? string.Format("{0:N1} ГБ, свободно {1:N1} ГБ", TotalMemoryGb, FreeMemoryGb) : "нет данных"),
                "SMART: " + (SmartSummary ?? "нет данных"),
                "",
                "Диски:"
            };
            if (!string.IsNullOrWhiteSpace(HwInfoVersion) || VrmTemperatureC.HasValue || PchTemperatureC.HasValue || Rail12V.HasValue || Rail5V.HasValue || Rail3V.HasValue || CpuVoltageV.HasValue)
                lines.Add("Датчики HWiNFO: " + (string.IsNullOrWhiteSpace(HwInfoVersion) ? "" : HwInfoVersion + " · ") +
                    "VRM " + (VrmTemperatureC.HasValue ? VrmTemperatureC.Value.ToString("N0") + " °C" : "нет данных") +
                    ", PCH " + (PchTemperatureC.HasValue ? PchTemperatureC.Value.ToString("N0") + " °C" : "нет данных") +
                    ", +12 В " + (Rail12V.HasValue ? Rail12V.Value.ToString("N2") + " В" : "нет данных") +
                    ", +5 В " + (Rail5V.HasValue ? Rail5V.Value.ToString("N2") + " В" : "нет данных") +
                    ", +3,3 В " + (Rail3V.HasValue ? Rail3V.Value.ToString("N2") + " В" : "нет данных") +
                    ", Vcore/VSoC " + (CpuVoltageV.HasValue ? CpuVoltageV.Value.ToString("N2") + " В" : "нет данных"));
            foreach (var disk in Disks)
                lines.Add(string.Format("  {0}: свободно {1:N1} из {2:N1} ГБ{3}", disk.Name,
                    disk.FreeBytes / 1073741824.0, disk.TotalBytes / 1073741824.0, disk.IsSystem ? " (системный)" : ""));
            foreach (var disk in SmartDisks)
                lines.Add("  " + disk.Model + ": " + disk.Health + " · " + disk.MediaType + " · " + disk.TransferMode +
                    (disk.PowerOnHours.HasValue ? " · " + disk.PowerOnHours.Value.ToString("N0") + " ч" : ""));
            if (Benchmark != null)
                lines.Add("Тест диска: " + (Benchmark.State == "Completed"
                    ? string.Format("чтение {0:N1}, запись {1:N1} МБ/с", Benchmark.ReadMbps, Benchmark.WriteMbps)
                    : Benchmark.Error ?? Benchmark.State));
            if (!string.IsNullOrWhiteSpace(CleanupSummary)) lines.Add("Очистка: " + CleanupSummary);
            lines.Add("Сеть: " + (Network.Count == 0 ? "нет активных адаптеров" : string.Join("; ", Network.Select(x =>
                x.Name + " · MAC " + (string.IsNullOrWhiteSpace(x.MacAddress) ? "не определён" : x.MacAddress) + " · IP " + x.Addresses))));
            lines.Add("Процессы: " + Processes.Count);
            lines.Add("");
            lines.Add("Требует внимания:");
            lines.AddRange(Findings.Count == 0 ? new[] { "  По доступным данным замечаний нет." } : Findings.Select(x => "  - " + x));
            lines.Add("");
            lines.Add("Не удалось проверить:");
            lines.AddRange(Unavailable.Count == 0 ? new[] { "  Нет." } : Unavailable.Select(x => "  - " + x));
            lines.Add("");
            lines.Add("Последние ошибки Windows:");
            lines.AddRange(Events.Count == 0 ? new[] { "  Не найдены или журнал недоступен." } :
                Events.Take(20).Select(x => string.Format("  {0} {1} | {2} #{3}: {4}", x.Time, x.Log, x.Source, x.Id, x.Message)));
            return string.Join(Environment.NewLine, lines);
        }
    }
}
