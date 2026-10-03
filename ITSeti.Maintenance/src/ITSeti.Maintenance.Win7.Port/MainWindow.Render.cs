using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using ITSeti.Maintenance.Win7;

namespace ITSeti.Maintenance.App
{
    public partial class MainWindow
    {
        private void Render(LegacySnapshot s)
        {
            var disks = s.Disks ?? new List<LegacyDisk>();
            var smart = s.SmartDisks ?? new List<LegacySmartDisk>();
            var findings = s.Findings ?? new List<string>();
            var unavailable = s.Unavailable ?? new List<string>();
            var userUnavailable = unavailable.Where(x => !x.StartsWith("Температура CPU:", StringComparison.OrdinalIgnoreCase)
                && !x.StartsWith("Тип памяти: Invalid query", StringComparison.OrdinalIgnoreCase)
                && !(x.StartsWith("Журнал ", StringComparison.OrdinalIgnoreCase)
                    && x.IndexOf("просмотрены только", StringComparison.OrdinalIgnoreCase) >= 0)
                && !(findings.Any(f => f.IndexOf("места", StringComparison.OrdinalIgnoreCase) >= 0)
                    && x.StartsWith("Тест скорости:", StringComparison.OrdinalIgnoreCase))).ToList();
            var system = disks.FirstOrDefault(x => x.IsSystem);
            var used = system != null && system.TotalBytes > 0 ? 100.0 * (system.TotalBytes - system.FreeBytes) / system.TotalBytes : 0;
            var memoryUsed = s.TotalMemoryGb > 0 ? 100.0 * (s.TotalMemoryGb.Value - (s.FreeMemoryGb ?? 0)) / s.TotalMemoryGb.Value : 0;
            var firstNetwork = (s.Network ?? new List<LegacyNetworkAdapter>()).FirstOrDefault();
            var benchmark = s.Benchmark;
            var temperature = s.CpuTemperatureC.HasValue ? " · " + s.CpuTemperatureC.Value.ToString("N0") + " °C" : "";
            var issueRows = findings.Select(x => new Win7Row { Title = x, Detail = "", Accent = Red }).ToList();
            var adminAudit = engineer ? LegacyAdminAccountAudit.Inspect() : "";
            var adminIssues = findings.Count + (adminAudit.StartsWith("Посторонние администраторы:", StringComparison.Ordinal) ? 1 : 0);

            view.Set("ComputerName", s.ComputerName ?? Environment.MachineName);
            view.Set("SnapshotDate", "Проверка от " + (s.CheckedAt ?? "неизвестно"));
            view.Set("Cpu", CpuValue(s.CpuPercent));
            view.Set("CpuDetail", (s.CpuName ?? "Процессор не определён") + temperature);
            view.Set("CpuStatusBrush", !s.CpuPercent.HasValue ? Blue : s.CpuTemperatureC >= 85 ? Red : s.CpuTemperatureC >= 75 ? Amber : Green);
            view.Set("Memory", Value(memoryUsed, "%"));
            view.Set("MemoryDetail", s.TotalMemoryGb.HasValue ? "Свободно " + s.FreeMemoryGb.GetValueOrDefault().ToString("N1") + " из " + s.TotalMemoryGb.Value.ToString("N1") + " ГБ" : "Нет данных");
            view.Set("MemoryStatusBrush", memoryUsed >= 90 ? Amber : Green);
            view.Set("DiskCount", system == null ? "нет данных" : used.ToString("N0") + "%");
            view.Set("DiskDetail", system == null ? "Системный диск не найден" : system.Name + " · свободно " + Gb(system.FreeBytes));
            view.Set("DiskStatusBrush", system != null && system.FreeBytes < 5L * 1073741824 ? Red : Green);
            view.Set("Gpu", s.GpuName ?? "Нет данных");
            view.Set("MemoryType", string.IsNullOrEmpty(s.MemoryType) ? "Не определён" : s.MemoryType);
            view.Set("OverviewUptimeLabel", s.ActiveUptimeHours.HasValue ? "Наработка после запуска: " + s.ActiveUptimeHours.Value.ToString("N0") + " ч" : "Последний запуск Windows: " + (s.LastBootAt ?? "нет данных"));
            view.Set("ScheduleStatus", string.IsNullOrEmpty(s.InventoryNumber) ? "Не указан" : s.InventoryNumber);
            view.Set("AdminInventoryInput", s.InventoryNumber ?? "");
            view.Set("AdminRmsLabel", s.RmsId ?? "Не найден");
            view.Set("AdminAnyDeskLabel", s.AnyDeskId ?? "Не найден");
            view.Set("SystemDiskSummary", system == null ? "Нет данных" : system.Name + " · свободно " + Gb(system.FreeBytes) + " из " + Gb(system.TotalBytes) + " · занято " + used.ToString("N0") + "%");
            view.Set("SystemDiskSpeedSummary", benchmark != null && benchmark.State == "Completed" ? "SEQ чт/зп " + benchmark.ReadMbps.GetValueOrDefault().ToString("N0") + "/" + benchmark.WriteMbps.GetValueOrDefault().ToString("N0") + " МБ/с" : "Скорость диска: " + (benchmark == null ? "тест не запускался" : benchmark.Error ?? "не проверена"));
            view.Set("BenchmarkReadBrush", benchmark != null && benchmark.State == "Completed" ? Green : Blue);
            view.Set("BenchmarkTitle", "DiskSpd · системный диск · 2 прохода");
            view.Set("BenchmarkRead", benchmark != null && benchmark.ReadMbps.HasValue ? benchmark.ReadMbps.Value.ToString("N0") + " МБ/с" : "Нет результата");
            view.Set("BenchmarkWrite", benchmark != null && benchmark.WriteMbps.HasValue ? benchmark.WriteMbps.Value.ToString("N0") + " МБ/с" : "Нет результата");
            view.Set("BenchmarkState", benchmark == null ? "Тест не запускался" : benchmark.Error ?? "Измерение завершено");
            view.Set("OverviewIssues", Rows(issueRows));
            view.Set("OverviewStatus", adminIssues == 0 ? "По доступным показателям замечаний нет" : "Требуют внимания: " + adminIssues);
            view.Set("OverviewCoverage", unavailable.Count == 0 ? "Все доступные показатели проверены." : "Не удалось проверить: " + string.Join("; ", unavailable));
            view.Set("AdminAccountAuditStatus", adminAudit);
            view.Set("UserVisibleIssues", Rows(issueRows));
            view.Set("UserIssueHeading", findings.Count == 0 ? "По доступным показателям замечаний нет" : "Что требует внимания");
            view.Set("UserIssueDetail", findings.Count == 0 ? "" : "Обнаружено: " + findings.Count);
            view.Set("UserCoverage", userUnavailable.Count == 0 ? "" : "Недоступные показатели: " + string.Join("; ", userUnavailable));
            view.Set("HasMoreUserIssues", false);
            view.Set("HasLowDiskSpace", findings.Any(x => x.IndexOf("места", StringComparison.OrdinalIgnoreCase) >= 0));
            view.Set("UserStatus", findings.Count > 0 ? "Проверка завершена. Есть замечания: " + findings.Count : userUnavailable.Count > 0 ? "Проверка завершена. Часть показателей недоступна." : "Проверка завершена. Замечаний нет.");
            view.Set("Status", "Проверка от " + (s.CheckedAt ?? "") + " · " + (s.Kind ?? ""));
            view.Set("UserLiveSummary", "");
            view.Set("UserCpuLabel", CpuValue(s.CpuPercent));
            view.Set("UserCpuValue", s.CpuPercent ?? 0d);
            view.Set("UserCpuNameAndTemperature", (s.CpuName ?? "Процессор") + temperature);
            view.Set("UserCpuDetail", "");
            view.Set("UserMemoryLabel", Value(memoryUsed, "%"));
            view.Set("UserMemoryValue", memoryUsed);
            view.Set("UserMemoryDetail", s.FreeMemoryGb.HasValue ? "Свободно " + s.FreeMemoryGb.Value.ToString("N1") + " ГБ" : "Нет данных");
            view.Set("UserDiskLabel", system == null ? "—" : used.ToString("N0") + "%");
            view.Set("UserDiskValue", used);
            view.Set("UserDiskDetail", system == null ? "Нет данных" : system.Name + " · свободно " + Gb(system.FreeBytes));
            view.Set("UserDiskHealth", smart.Count == 0 ? "SMART недоступен" : "SMART: " + (s.SmartSummary ?? smart[0].Health));
            view.Set("UserInventoryLabel", "Инв. номер: " + (s.InventoryNumber ?? "не указан"));
            view.Set("UserRmsLabel", "RMS: " + (s.RmsId ?? "не найден"));
            view.Set("UserAnyDeskLabel", "AnyDesk: " + (s.AnyDeskId ?? "не найден"));
            view.Set("UserIpLabel", firstNetwork == null ? "" : firstNetwork.Addresses);
            view.Set("UserIpTooltip", firstNetwork == null ? "" : firstNetwork.Name);
            view.Set("UserUptimeLabel", s.ActiveUptimeHours.HasValue ? "Наработка после запуска: " + s.ActiveUptimeHours.Value.ToString("N0") + " ч" : "Последний запуск Windows: " + (s.LastBootAt ?? "нет данных"));
            view.Set("HasInventoryNumber", !string.IsNullOrEmpty(s.InventoryNumber));
            view.Set("HasRmsId", !string.IsNullOrEmpty(s.RmsId));
            view.Set("HasAnyDeskId", !string.IsNullOrEmpty(s.AnyDeskId));

            var diskRows = new List<Win7Row>();
            foreach (var item in smart)
            {
                var row = new Win7Row { Model = item.Model, MediaType = item.MediaType, Health = "SMART: " + item.Health,
                    HealthBrush = item.Health != null && item.Health.IndexOf("хорошо", StringComparison.OrdinalIgnoreCase) >= 0 ? Green : Amber,
                    TransferMode = item.TransferMode, HasPowerOnHours = item.PowerOnHours.HasValue,
                    LifetimeLabel = item.PowerOnHours.HasValue ? item.PowerOnHours.Value.ToString("N0") + " ч (около " + (item.PowerOnHours.Value / 8760) + " г. " + (item.PowerOnHours.Value % 8760 / 24) + " дн.)" : "",
                    LifetimeWarning = item.PowerOnHours >= 60000 ? "Наработка более 60 000 ч" : "", Partitions = new ObservableCollection<Win7Row>() };
                foreach (var partition in disks.Where(x => !string.IsNullOrEmpty(item.Letters) && item.Letters.IndexOf(x.Name, StringComparison.OrdinalIgnoreCase) >= 0))
                    row.Partitions.Add(new Win7Row { Name = partition.Name, Capacity = Gb(partition.FreeBytes) + " из " + Gb(partition.TotalBytes) + " свободно", Usage = partition.TotalBytes > 0 ? (100.0 * (partition.TotalBytes - partition.FreeBytes) / partition.TotalBytes).ToString("N0") + "% занято" : "" });
                diskRows.Add(row);
            }
            foreach (var partition in disks.Where(x => !diskRows.Any(g => g.Partitions.Any(p => p.Name == x.Name))))
                diskRows.Add(new Win7Row { Model = partition.Name + (partition.IsSystem ? " · системный диск" : ""), MediaType = "", Health = "SMART для этого раздела не сопоставлен", HealthBrush = Amber,
                    Partitions = Rows(new[] { new Win7Row { Name = partition.Name, Capacity = Gb(partition.FreeBytes) + " из " + Gb(partition.TotalBytes) + " свободно", Usage = partition.TotalBytes > 0 ? (100.0 * (partition.TotalBytes - partition.FreeBytes) / partition.TotalBytes).ToString("N0") + "% занято" : "" } }) });
            view.Set("DiskGroups", Rows(diskRows.OrderByDescending(x => x.Partitions.Any(p => system != null && p.Name == system.Name))));
            var volumes = Rows(disks.Select(x => new Win7Row { Name = x.Name }));
            view.Set("TreeSizeVolumes", volumes);
            view.Set("SelectedTreeSizeVolume", volumes.FirstOrDefault(x => system != null && x.Name == system.Name));
            view.Set("NetworkAdapters", Rows((s.Network ?? new List<LegacyNetworkAdapter>()).Select(x => new Win7Row { Name = x.Name, Type = x.Kind, Status = x.Status, LinkSpeed = x.Speed, Addresses = x.Addresses })));
            view.Set("Processes", Rows((s.Processes ?? new List<LegacyProcess>()).Select(x => new Win7Row { Name = x.Name,
                Description = x.Path == null || x.Path == "нет доступа" ? "Нет доступа" : System.IO.Path.GetFileName(x.Path),
                Publisher = "—", Signature = "Не проверена" })));
            view.Set("ProcessStatus", "Процессов: " + (s.Processes == null ? 0 : s.Processes.Count));
            var progress = view.Get<ObservableCollection<Win7Row>>("CheckProgressEntries");
            if (progress.Count == 0)
            {
                progress.Add(new Win7Row { Time = s.CheckedAt, Source = "Проверка", Message = "Сохранённый отчёт: " + (s.Kind ?? "") });
                progress.Add(new Win7Row { Time = "", Source = "SMART", Message = s.SmartSummary ?? "Нет данных" });
                progress.Add(new Win7Row { Time = "", Source = "Диск", Message = benchmark == null ? "Тест не запускался" : benchmark.Error ?? "Тест завершён" });
                view.Set("CheckProgressPhase", "Последняя проверка от " + s.CheckedAt);
            }
            RenderEvents();
        }

        private void RenderEvents()
        {
            if (current == null) return;
            var events = current.Events ?? new List<LegacyEvent>();
            var rows = events.Select(x => new Win7Row { Time = x.Time, LevelLabel = "Ошибка", Provider = x.Source, Id = x.Id, Message = x.Message }).ToList();
            view.Set("FilteredEvents", Rows(rows));
            view.Set("EventStatus", "Событий: " + rows.Count);
        }
    }
}
