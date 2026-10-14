// Compatibility properties used by existing WPF bindings; never part of report JSON.
#if NETFRAMEWORK
using JsonIgnore = System.Web.Script.Serialization.ScriptIgnoreAttribute;
#else
using JsonIgnore = System.Text.Json.Serialization.JsonIgnoreAttribute;
#endif
namespace ITSeti.Maintenance.Core;

public sealed partial record DiskSnapshot
{
    [JsonIgnore]
    public double UsedPercent => TotalBytes > 0 ? 100.0 * (TotalBytes - FreeBytes) / TotalBytes : 0;
    [JsonIgnore]
    public string Capacity => $"{FreeBytes / 1073741824.0:N1} из {TotalBytes / 1073741824.0:N1} ГБ свободно";
    [JsonIgnore]
    public string Usage => $"{UsedPercent:N0}%";
    [JsonIgnore]
    public string Status => FreeBytes < 5L * 1073741824 ? "Почти нет свободного места"
        : FreeBytes < 15L * 1073741824 ? "Мало свободного места" : "Достаточно места";

}

public sealed partial record DiagnosticSnapshot
{
    [JsonIgnore]
    public double MemoryUsedPercent => TotalMemoryBytes > 0
        ? 100.0 * (TotalMemoryBytes - AvailableMemoryBytes) / TotalMemoryBytes : 0;
    [JsonIgnore]
    public int AlertCount => DiagnosticRules.GetFindings(this, includeDiskLinkWarnings: false).Count;
    [JsonIgnore]
    public string DateLabel => StartedAt.LocalDateTime.ToString("dd.MM.yyyy HH:mm:ss");
    [JsonIgnore]
    public string CpuLabel => CpuPercent < 0 ? "Нет данных" : $"{CpuPercent:N0}%";
    [JsonIgnore]
    public string MemoryLabel => TotalMemoryBytes == 0 ? "Нет данных" : $"{MemoryUsedPercent:N0}%";
    [JsonIgnore]
    public bool IsQuickFull => Full?.Benchmark is { State: "Skipped", Error: var reason } &&
        reason.Contains("Быстрая проверка", StringComparison.OrdinalIgnoreCase);
    [JsonIgnore]
    public string KindLabel => Full is null || IsQuickFull ? "Быстрая" : "Полная";
    [JsonIgnore]
    public string ResultLabel
    {
        get
        {
            var incomplete = Full is { } full && ((!IsQuickFull && full.Benchmark.State != "Completed") || full.SmartDisks.Count == 0);
            var result = AlertCount > 0 ? $"Требует внимания: {AlertCount}" : "Замеренные показатели в норме";
            return incomplete ? result + " · неполные данные" : result;
        }
    }

    [JsonIgnore]
    public string HistoryDetailLabel
    {
        get
        {
            var findings = DiagnosticRules.GetFindings(this, includeDiskLinkWarnings: false)
                .Select(CondenseHistoryFact)
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            var details = findings.Take(2).ToList();
            if (Full is { } full)
            {
                details.AddRange(DiagnosticRules.GetActionableEvents(full.Events).Where(item => item.Level <= 2).OrderBy(item => item.Level)
                    .Take(Math.Max(0, 3 - details.Count)).Select(item => $"Ошибка Windows: {item.Provider} #{item.Id}"));
            }
            if (details.Count < 3) details.AddRange(findings.Skip(details.Count).Take(3 - details.Count));

            if (details.Count == 0) return ResultLabel;
            var result = string.Join(" · ", details.Distinct(StringComparer.CurrentCultureIgnoreCase));
            return result.Length <= 260 ? result : result.Substring(0, 257) + "...";
        }
    }

    private static string CondenseHistoryFact(string finding)
    {
        var sentenceEnd = finding.IndexOf(". ", StringComparison.Ordinal);
        return sentenceEnd < 0 ? finding : finding.Substring(0, sentenceEnd + 1);
    }

}

public sealed partial record ProcessDetails
{
    [JsonIgnore]
    public bool CanTrustPublisher => Signature.Equals("Valid", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(Publisher);

}

public sealed partial record MemoryProcessDetails
{
    [JsonIgnore]
    public string MemoryLabel => $"{WorkingSetBytes / 1073741824.0:N1} ГБ";

}

public sealed partial record EventDetails
{
    [JsonIgnore]
    public string LevelLabel => Level switch { 1 => "Критическая", 2 => "Ошибка", 3 => "Предупреждение", _ => "Событие" };

}

public sealed partial record DiskBenchmark
{
    [JsonIgnore]
    public string ReadLabel => Read.HasValue ? $"{Read:N1} МБ/с" : "Нет результата";
    [JsonIgnore]
    public string WriteLabel => Write.HasValue ? $"{Write:N1} МБ/с" : "Нет результата";

}

public sealed partial record HwInfoSnapshot
{
    [JsonIgnore]
    public double? CpuTemperatureC => Sensors.Where(item => item.Category == "CpuTemperature").Select(item => (double?)item.Value).OrderByDescending(item => item).FirstOrDefault();
    [JsonIgnore]
    public double? VrmTemperatureC => Sensors.Where(item => item.Category == "VrmTemperature").Select(item => (double?)item.Value).OrderByDescending(item => item).FirstOrDefault();
    [JsonIgnore]
    public double? PchTemperatureC => Sensors.Where(item => item.Category == "PchTemperature").Select(item => (double?)item.Value).OrderByDescending(item => item).FirstOrDefault();
    [JsonIgnore]
    public double? Rail12V => SelectRail("Rail12V", 12.0);
    [JsonIgnore]
    public double? Rail5V => SelectRail("Rail5V", 5.0);
    [JsonIgnore]
    public double? Rail3V => SelectRail("Rail3V", 3.3);
    [JsonIgnore]
    public double? MemoryLoadPercent => Sensors.FirstOrDefault(item => item.Category == "MemoryLoad")?.Value;
    [JsonIgnore]
    public double? CpuVoltageV => Sensors.Where(item => item.Category == "CpuVoltage").Select(item => (double?)item.Value).OrderByDescending(item => item).FirstOrDefault();
    [JsonIgnore]
    public double? CpuLoadPercent => Sensors.Where(item => item.Category == "CpuLoad")
        .OrderByDescending(item => item.Name.Contains("total cpu usage",StringComparison.OrdinalIgnoreCase)?2:item.Name.Contains("total", StringComparison.OrdinalIgnoreCase) || item.Name.Contains("суммар", StringComparison.OrdinalIgnoreCase)?1:0)
        .ThenByDescending(item=>item.Value)
        .Select(item => (double?)item.Value).FirstOrDefault();
    [JsonIgnore]
    public IReadOnlyList<HwInfoSensorReading> AlertingSensors => Sensors.Where(item => item.IsWarning || item.IsCritical).ToArray();

    private double? SelectRail(string category, double nominal) => Sensors
        .Where(item => item.Category == category && Numeric.IsFinite(item.Value))
        .Select(item => (double?)item.Value)
        .OrderBy(value => Math.Abs(value!.Value - nominal))
        .FirstOrDefault();

}
public sealed partial record UserIssue
{
    [JsonIgnore]
    public int Priority => Severity switch { "Critical" => 3, "Warning" => 2, _ => 1 };
    [JsonIgnore]
    public string Accent => Severity switch { "Critical" => "#D94F4F", "Warning" => "#DA991D", _ => "#326EFF" };
}
