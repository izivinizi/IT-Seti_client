using System.Text.RegularExpressions;

namespace ITSeti.Maintenance.Core;

public static class DiagnosticRules
{
    public static IReadOnlyList<UserIssue> GetUserIssues(DiagnosticSnapshot snapshot)
    {
        var issues = new List<UserIssue>();
        var full = snapshot.Full;
        if (snapshot.LastBootAt is { } boot && snapshot.StartedAt - boot > TimeSpan.FromDays(6))
            issues.Add(new("Компьютер давно не перезагружали",
                "Последняя перезагрузка была более 6 дней назад. Сохраните открытые документы и перезагрузите компьютер, когда закончите работу.", "Warning"));
        if (snapshot.WindowsBuild is int windowsBuild && windowsBuild < 17763)
            issues.Add(new("Версия Windows устарела", $"Установлена {FormatWindows(snapshot)}. Это ниже Windows 10 версии 1809 (сборка 17763); система давно не получает актуальную поддержку и обновления безопасности.", "Warning"));
        var sample = full?.ResourceSampling is { Samples: >= 6, Error: "" } complete ? complete : null;
        if (snapshot.TotalMemoryBytes > 0)
        {
            var total = snapshot.TotalMemoryBytes / 1073741824.0;
            if (total <= 4.5)
                issues.Add(new("Небольшой объём памяти", "У компьютера 4 ГБ памяти или меньше. Если открыто несколько программ, он может работать медленнее.", "Warning"));
            if (sample is not null && sample.LowAvailableSamples >= 5 && sample.PagingHighSamples >= 3)
            {
                var users = full?.TopMemoryProcesses?.Take(3)
                    .Select(p => $"{p.DisplayName} ({p.MemoryLabel})").ToArray() ?? [];
                var detail = "Свободной памяти почти не осталось. Компьютер переносит данные на диск, поэтому программы могут заметно тормозить.";
                if (users.Length > 0) detail += " Больше всего памяти занимают: " + string.Join(", ", users) + ".";
                issues.Add(new("Компьютеру не хватает памяти", detail, "Critical"));
            }
            else if (sample is not null && sample.MemoryHighSamples >= 5)
            {
                var users = full?.TopMemoryProcesses?.Take(3)
                    .Select(p => $"{p.DisplayName} ({p.MemoryLabel})").ToArray() ?? [];
                var detail = "Память была занята почти полностью в течение проверки. При переключении между программами возможны задержки.";
                if (users.Length > 0) detail += " Больше всего памяти занимают: " + string.Join(", ", users) + ".";
                issues.Add(new("Память почти постоянно занята", detail, "Warning"));
            }
        }
        if (sample is not null && sample.CpuHighSamples >= 5)
            issues.Add(new("Процессор долго работал на пределе", "Нагрузка держалась выше 90% большую часть проверки. Из-за этого программы могут отвечать медленно.", "Warning"));
        foreach (var disk in snapshot.Disks.Where(d => d.TotalBytes > 0 && d.FreeBytes < 15L * 1073741824))
        {
            var severe = disk.FreeBytes < 5L * 1073741824;
            issues.Add(new($"На диске {disk.Name} мало места", $"Свободно {disk.FreeBytes / 1073741824.0:N1} ГБ. Можно выполнить очистку или посмотреть, какие файлы занимают место.", severe ? "Critical" : "Warning"));
        }
        foreach (var disk in (full?.PhysicalDisks ?? snapshot.QuickDisks ?? []).Where(d => HasBadWindowsHealth(d.Health)))
            issues.Add(new($"Диск {disk.Model} требует проверки", "Windows сообщает о проблеме с накопителем. Сохраните важные файлы и обратитесь к специалисту.", "Critical"));
        if (full is not null)
        {
            foreach (var disk in full.SmartDisks.Where(d => Regex.IsMatch(d.Status, "Caution|Bad|Тревог|Плох", RegexOptions.IgnoreCase)))
                issues.Add(new($"Состояние диска {disk.Model} вызывает опасения", "Сохраните важные файлы и обратитесь к специалисту для проверки диска.", "Critical"));
            var benchmark = full.Benchmark;
            var warning = GetReadWarningThreshold(snapshot, benchmark);
            var criticalSpeed = benchmark.MediaType switch { "SSD" => 180, "HDD" => 80, _ => 0 };
            if (benchmark.State == "Completed" && warning > 0 && benchmark.Read is double read && read < warning)
                issues.Add(new("Системный диск читает данные медленно", $"Измерено {read:N0} МБ/с, ориентир для этого типа диска — от {warning} МБ/с. Результат стоит перепроверить, когда компьютер не занят другими задачами.", read < criticalSpeed ? "Critical" : "Warning"));
            if (benchmark.MediaType == "HDD")
                issues.Add(new("Система установлена на обычном жёстком диске", "Программы могут запускаться медленнее, чем на твердотельном диске. Это не неисправность.", "Warning"));
            foreach (var disk in full.SmartDisks.Where(d => d.MediaType == "SSD" && IsLinkLimited(d.TransferMode) && d.TransferMode.StartsWith("SATA/", StringComparison.OrdinalIgnoreCase)))
                issues.Add(new($"Диск {disk.Model} подключён не в самом быстром режиме", "Сам диск поддерживает более быстрое подключение. Специалист может проверить кабель и порт компьютера.", "Warning"));
            if (sample is not null && benchmark.MediaType is ("SSD" or "HDD"))
            {
                var latency = Math.Max(sample.DiskReadLatencyMs ?? 0, sample.DiskWriteLatencyMs ?? 0);
                var operations = sample.DiskReadOperations + sample.DiskWriteOperations;
                var limit = benchmark.MediaType == "HDD" ? 80 : 25;
                if (operations >= 100 && latency >= limit)
                    issues.Add(new("Системный диск долго отвечает", $"В среднем ответ занимал {latency:N0} мс. Это может замедлять открытие файлов и программ; краткие скачки задержки не считаются неисправностью.", "Warning"));
            }
        }
        return issues.OrderByDescending(i => i.Priority).ToArray();
    }

    public static IReadOnlyList<string> GetFindings(DiagnosticSnapshot snapshot)
    {
        var findings = new List<string>();
        if (snapshot.LastBootAt is { } boot && snapshot.StartedAt - boot > TimeSpan.FromDays(6))
            findings.Add($"Windows не перезагружалась {(int)(snapshot.StartedAt - boot).TotalDays} дней. Перед перезагрузкой сохраните открытые документы.");
        if (snapshot.WindowsBuild is int windowsBuild && windowsBuild < 17763)
            findings.Add($"Windows ниже версии 1809: {FormatWindows(snapshot)}.");
        var sample = snapshot.Full?.ResourceSampling is { Samples: >= 6, Error: "" } complete ? complete : null;
        if (sample?.CpuHighSamples >= 5) findings.Add($"CPU: ≥90% в {sample.CpuHighSamples} из {sample.Samples} замеров за 30 секунд");
        if (sample?.MemoryHighSamples >= 5) findings.Add($"ОЗУ: ≥90% в {sample.MemoryHighSamples} из {sample.Samples} замеров за 30 секунд");
        if (sample is not null && sample.LowAvailableSamples >= 5 && sample.PagingHighSamples >= 3)
            findings.Add("ОЗУ: менее 500 МБ доступно и активная подкачка");
        if (snapshot.TotalMemoryBytes > 0 && snapshot.TotalMemoryBytes <= 4.5 * 1073741824) findings.Add("ОЗУ: 4 ГБ или меньше");
        findings.AddRange(snapshot.Disks.Where(d => d.FreeBytes < 15L * 1073741824).Select(d => $"{d.Name} свободно {d.FreeBytes / 1073741824.0:N1} ГБ"));
        var full = snapshot.Full;
        foreach (var disk in (full?.PhysicalDisks ?? snapshot.QuickDisks ?? []).Where(d => HasBadWindowsHealth(d.Health)))
            findings.Add($"Windows: тревожное состояние {disk.Model}: {disk.Health}");
        if (full is null) return findings;
        var critical = full.Events.Count(e => e.Level == 1);
        if (critical > 0) findings.Add($"Критических событий в доступной выборке: {critical}");
        foreach (var disk in full.SmartDisks)
        {
            if (Regex.IsMatch(disk.Status, "Caution|Bad|Тревог|Плох", RegexOptions.IgnoreCase))
                findings.Add($"SMART {disk.Model}: {disk.Status}");
            if (disk.MediaType == "SSD" && IsLinkLimited(disk.TransferMode)) findings.Add($"Ограничение интерфейса {disk.Model}: {disk.TransferMode}. Поддержка порта/слота ПК не подтверждена.");
        }
        var hdds = full.PhysicalDisks.Where(d => d.MediaType == "HDD").Select(d => d.Model)
            .Concat(full.SmartDisks.Where(d => d.MediaType == "HDD").Select(d => d.Model)).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var model in hdds) findings.Add($"{model}: HDD, не SSD. Запуск программ и работа с мелкими файлами могут быть медленнее даже при нормальной скорости чтения.");
        var benchmark = full.Benchmark;
        var threshold = GetReadWarningThreshold(snapshot, benchmark);
        if (benchmark.State == "Completed" && threshold > 0 && benchmark.Read is double read && read < threshold)
            findings.Add($"{benchmark.Drive} {benchmark.MediaType}: чтение {read:N1} МБ/с, ниже {threshold} МБ/с.");
        if (sample is not null && benchmark.MediaType is ("SSD" or "HDD"))
        {
            var latency = Math.Max(sample.DiskReadLatencyMs ?? 0, sample.DiskWriteLatencyMs ?? 0);
            if (sample.DiskReadOperations + sample.DiskWriteOperations >= 100 && latency >= (benchmark.MediaType == "HDD" ? 80 : 25))
                findings.Add($"{benchmark.Drive}: средняя задержка диска {latency:N0} мс за 30 секунд; проверьте при обычной нагрузке.");
        }
        return findings;
    }

    private static int GetReadWarningThreshold(DiagnosticSnapshot snapshot, DiskBenchmark benchmark)
    {
        if (benchmark.MediaType == "HDD") return 100;
        if (benchmark.MediaType != "SSD") return 0;
        if (benchmark.IsNvme || snapshot.Full?.SmartDisks.Any(d =>
                d.MediaType == "SSD" && Regex.IsMatch(d.TransferMode, "PCIe|NVMe|NVM Express", RegexOptions.IgnoreCase) &&
                Regex.Matches(d.Letters, @"(?i)(?<![A-Z])[A-Z]:").Cast<Match>()
                    .Any(m => string.Equals(m.Value, Environment.GetEnvironmentVariable("SystemDrive"), StringComparison.OrdinalIgnoreCase))) == true)
            return 900;
        return 210;
    }

    private static string FormatWindows(DiagnosticSnapshot snapshot)
    {
        var edition = string.IsNullOrWhiteSpace(snapshot.WindowsEdition) ? "Windows" : snapshot.WindowsEdition;
        var release = string.IsNullOrWhiteSpace(snapshot.WindowsRelease) ? "версия неизвестна" : "версия " + snapshot.WindowsRelease;
        return $"{edition}, {release}, сборка {snapshot.WindowsBuild}";
    }

    public static bool IsLinkLimited(string mode)
    {
        var sata = Regex.Match(mode, @"^SATA/(150|300|600)\s*\|\s*SATA/(150|300|600)$");
        if (sata.Success) return int.Parse(sata.Groups[1].Value) < int.Parse(sata.Groups[2].Value);
        var pcie = Regex.Match(mode, @"^PCIe ([1-6])\.0 x(1|2|4|8|16)\s*\|\s*PCIe ([1-6])\.0 x(1|2|4|8|16)$");
        return pcie.Success && (int.Parse(pcie.Groups[1].Value) < int.Parse(pcie.Groups[3].Value)
            || int.Parse(pcie.Groups[2].Value) < int.Parse(pcie.Groups[4].Value));
    }

    private static bool HasBadWindowsHealth(string health) =>
        Regex.IsMatch(health, "Warning|Unhealthy|Degraded|Pred Fail|Error|Тревог|Плох", RegexOptions.IgnoreCase);
}

public sealed record UserIssue(string Title, string Detail, string Severity)
{
    public int Priority => Severity switch { "Critical" => 3, "Warning" => 2, _ => 1 };
    public string Accent => Severity switch { "Critical" => "#D94F4F", "Warning" => "#DA991D", _ => "#326EFF" };
}
