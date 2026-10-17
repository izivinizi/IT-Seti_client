using System.Text.Json;
using System.Text.RegularExpressions;
using ITSeti.Maintenance.Core;

namespace ITSeti.Server;

public sealed record MonitorIssue(string Category, string Identity, string Title, string Detail, short Severity);

public static class Monitoring
{
    // Applies to older clients too; resource pressure is visible when warnings are selected.
    public const string AlertSqlPredicate = "a.severity IN (1,2)";
    public static bool IsResourcePressure(string title, string detail = "") =>
        Regex.IsMatch(title, @"загрузк[аи] (CPU|ЦП|процессора|памяти|ОЗУ)|мало (доступной памяти|места)|небольшой объём памяти", RegexOptions.IgnoreCase) ||
        (title == "Новое замечание диагностики" && (SpaceNote.IsMatch(detail) ||
            detail.StartsWith("ОЗУ:", StringComparison.OrdinalIgnoreCase) ||
            detail.StartsWith("CPU:", StringComparison.OrdinalIgnoreCase) && !detail.Contains("температур", StringComparison.OrdinalIgnoreCase)));
    private static readonly JsonSerializerOptions SnapshotJsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static IReadOnlyList<MonitorIssue> CurrentIssues(JsonElement report) => Extract(report);
    public static IReadOnlyList<MonitorIssue> AlertsForReport(JsonElement current, JsonElement? previous,
        JsonElement? previousEvents = null, IReadOnlyList<MonitorIssue>? currentIssues = null)
    {
        currentIssues ??= Extract(current);
        // Historical Windows events still need a baseline; current diagnostic findings do not.
        return currentIssues.Where(issue => issue.Category != "event" && issue.Severity is 1 or 2)
            .Concat(NewIssues(current, previous, previousEvents, includeWarnings: true, currentIssues: currentIssues))
            .DistinctBy(issue => (issue.Category.ToLowerInvariant(), issue.Identity.ToLowerInvariant()))
            .ToArray();
    }
    private static readonly Regex SpaceNote = new(@"^([A-Z]:\\?)\s+свободно\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex SpaceAmount = new(@"\bсвободно\s+([0-9]+(?:[.,][0-9]+)?)\s*ГБ", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex BugcheckCode = new(@"\bBugcheckCode\s*[:=]\s*(\d+)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex FindingBugcheck = new(@"\bКод\s+(\d+)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    // The ingestion path already needs current issues for the report summary.
    // Keep that evaluation for alert comparison instead of parsing the report twice.
    public static IReadOnlyList<MonitorIssue> NewIssues(JsonElement current, JsonElement? previous,
        JsonElement? previousEvents = null, bool includeWarnings = false, IReadOnlyList<MonitorIssue>? currentIssues = null)
    {
        currentIssues ??= Extract(current);
        if (previous is null)
        {
            var isConfirmedServiceAlert = JsonRead.String(current, "KindLabel", "Kind", "kind") == "Постоянный мониторинг" &&
                JsonRead.Property(current, "DiagnosticIssues") is { ValueKind: JsonValueKind.Array };
            return currentIssues.Where(issue => !IsResourcePressure(issue.Title, issue.Detail) &&
                (issue.Category == "identity" || isConfirmedServiceAlert && issue.Severity == 1)).ToArray();
        }
        var eventBaseline = previousEvents ?? previous;
        var previousIssues = Extract(previous.Value);
        var previousEventIssues = previousEvents is null || previousEvents.Value.Equals(previous.Value)
            ? previousIssues : Extract(previousEvents.Value);
        var oldIssues = previousIssues.Where(i => i.Category != "event")
            .Concat(previousEventIssues.Where(i => i.Category == "event"));
        var oldKeys = oldIssues.Select(i => i.Category + "|" + i.Identity + "|" + i.Severity).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hadEventBaseline = eventBaseline is not null && EventArray(eventBaseline.Value) is { ValueKind: JsonValueKind.Array };
        return currentIssues.Where(i => !IsResourcePressure(i.Title, i.Detail) && (i.Category == "identity" || i.Severity == 1 || includeWarnings) && (i.Category != "event" || hadEventBaseline) &&
            !oldKeys.Contains(i.Category + "|" + i.Identity + "|" + i.Severity)).ToArray();
    }

    public static MonitorIssue? RmsChangeIssue(string? previous, string? current)
    {
        previous = previous?.Trim();
        current = current?.Trim();
        if (string.IsNullOrWhiteSpace(previous) || string.IsNullOrWhiteSpace(current) ||
            string.Equals(previous, current, StringComparison.OrdinalIgnoreCase)) return null;
        return new("identity", "rms-id", "Изменился номер RMS", $"Ранее: {Clip(previous, 80)} · сейчас: {Clip(current, 80)}", 2);
    }

    public static MonitorIssue DuplicateInventoryIssue(string inventory, IEnumerable<string> otherComputerNames)
    {
        var names = otherComputerNames.Select(name => Clip(name.Trim(), 100)).Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToArray();
        return new("identity", "inventory:" + inventory, "Инвентарный номер используется несколькими ПК",
            $"Номер {Clip(inventory, 80)} уже указан у: {string.Join(", ", names)}.", 2);
    }

    private static IReadOnlyList<MonitorIssue> Extract(JsonElement report)
    {
        var issues = new Dictionary<string, MonitorIssue>(StringComparer.OrdinalIgnoreCase);
        var serverIdentityIssues = JsonRead.Property(report, "ServerIdentityIssues", "serverIdentityIssues");
        if (serverIdentityIssues is { ValueKind: JsonValueKind.Array })
            foreach (var item in serverIdentityIssues.Value.EnumerateArray())
            {
                var identity = JsonRead.String(item, "Identity") ?? "identity:unknown";
                var issue = new MonitorIssue("identity", identity,
                    JsonRead.String(item, "Title") ?? "Проблема идентификации ПК",
                    Clip(JsonRead.String(item, "Detail") ?? "", 700), 2);
                issues.TryAdd(issue.Category + "|" + issue.Identity, issue);
            }
        // New reports carry raw inputs plus a versioned result. Always evaluate inputs using the same Core as the client.
        if (JsonRead.Property(report, "DiagnosticResult") is { ValueKind: JsonValueKind.Object })
        {
            var raw = JsonRead.Property(report, "DiagnosticSnapshot") ?? report;
            try
            {
                var snapshot = raw.Deserialize<DiagnosticSnapshot>(SnapshotJsonOptions);
                if (snapshot is not null && snapshot.Disks is not null && snapshot.Notes is not null)
                {
                    foreach (var item in DiagnosticEngine.Evaluate(snapshot).Issues)
                    {
                        var issue = ClientIssue(item.Title, item.Detail, item.Severity == "Critical" ? (short)1 : (short)2);
                        issues.TryAdd(issue.Category + "|" + issue.Identity, issue);
                    }
                    return issues.Values.ToArray();
                }
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException or NullReferenceException) { }
        }
        var clientIssues = JsonRead.Property(report, "DiagnosticIssues");
        if (clientIssues is { ValueKind: JsonValueKind.Array })
        {
            foreach (var issue in clientIssues.Value.EnumerateArray().Where(i => JsonRead.String(i, "Severity") is "Critical" or "Warning")
                .Select(i => ClientIssue(JsonRead.String(i, "Title") ?? "", JsonRead.String(i, "Detail") ?? "",
                    JsonRead.String(i, "Severity") == "Critical" ? (short)1 : (short)2)))
                issues.TryAdd(issue.Category + "|" + issue.Identity, issue);
            return issues.Values.ToArray();
        }
        if (JsonRead.Property(report, "Notes") is { ValueKind: JsonValueKind.Array } &&
            JsonRead.Property(report, "Disks") is { ValueKind: JsonValueKind.Array })
        {
            try
            {
                var snapshot = report.Deserialize<DiagnosticSnapshot>(SnapshotJsonOptions);
                if (snapshot is not null)
                    return DiagnosticRules.GetUserIssues(snapshot).Select(i => ClientIssue(i.Title, i.Detail,
                        i.Severity == "Critical" ? (short)1 : (short)2)).ToArray();
            }
            catch (Exception ex) when (ex is JsonException or NullReferenceException or ArgumentException) { }
        }
        foreach (var note in JsonRead.StringList(report, "Notes").Concat(JsonRead.StringList(report, "Findings")).Distinct())
        {
            var text = Clip(note.Trim(), 2000);
            if (!Regex.IsMatch(text, @"^(HWiNFO критично:|HWiNFO предупреждение:|CPU:|ОЗУ:|SMART[ :]|Windows: тревожное|Windows ниже версии|Наработка |[A-Z]:\\?\s+свободно|Произошла критическая ошибка Windows)", RegexOptions.IgnoreCase)) continue;
            if (text.Length == 0 || text.StartsWith("Критических событий в доступной выборке:", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("Проверка выполнена", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("Ограничение интерфейса", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("HDD, не SSD", StringComparison.OrdinalIgnoreCase)) continue;
            var identity = NoteIdentity(text);
            var space = SpaceAmount.Match(text);
            var criticalSpace = space.Success && double.TryParse(space.Groups[1].Value.Replace(',', '.'),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var freeGb) && freeGb < 5;
            var severity = criticalSpace || text.Contains("критически", StringComparison.OrdinalIgnoreCase) || text.StartsWith("HWiNFO критично:",StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("SMART ", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("Windows: тревожное состояние", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("Произошла критическая ошибка Windows", StringComparison.OrdinalIgnoreCase) ? (short)1 : (short)2;
            var issue = new MonitorIssue("finding", identity, "Новое замечание диагностики", Clip(text, 700), severity);
            issues.TryAdd(issue.Category + "|" + issue.Identity, issue);
        }

        var events = EventArray(report);
        if (events is { ValueKind: JsonValueKind.Array })
        {
            var items = events.Value.EnumerateArray().Take(500).ToArray();
            var zeroPowerLosses = items.Count(IsZeroPowerLoss);
            foreach (var item in items)
            {
                if (zeroPowerLosses is > 0 and < 3 && IsZeroPowerLoss(item)) continue;
                var level = Level(item);
                if (level != 1) continue;
                var log = Clip(JsonRead.String(item, "Log")?.Trim() ?? "Windows", 120);
                var provider = Clip(JsonRead.String(item, "Provider", "Source")?.Trim() ?? "Журнал", 120);
                var id = Clip(JsonRead.String(item, "Id")?.Trim() ?? "?", 32);
                var message = Clip(JsonRead.String(item, "Message")?.Trim() ?? "", 2048);
                var code = IsKernelPower41(item) ? BugcheckCode.Match(message).Groups[1].Value : "";
                var identity = string.Join('|', log, provider, id, level, code).ToLowerInvariant();
                var title = $"{provider} #{id}";
                var detail = string.Join(" · ", new[] { log, JsonRead.String(item, "Time"), message }.Where(s => !string.IsNullOrWhiteSpace(s)));
                if (uint.TryParse(code, out var bugcheck) && bugcheck != 0)
                {
                    title = $"Синий экран: {BugcheckCodeCatalog.GetName(bugcheck)}";
                    detail = $"Код {bugcheck} ({BugcheckCodeCatalog.FormatCode(bugcheck)}). {BugcheckCodeCatalog.GetDescription(bugcheck)} · " + detail;
                }
                var issue = new MonitorIssue("event", identity, Clip(title, 180), Clip(detail, 1000), (short)level);
                issues.TryAdd(issue.Category + "|" + issue.Identity, issue);
            }
        }
        return issues.Values.ToArray();
    }

    private static string NoteIdentity(string text)
    {
        if(text.StartsWith("HWiNFO",StringComparison.OrdinalIgnoreCase))return Regex.Replace(text,@"\s+[-+]?\d+(?:[.,]\d+)?\s*(?:°C|%|В|ГБ)\.?\s*$","").ToLowerInvariant();
        var space = SpaceNote.Match(text);
        if (space.Success) return "space:" + space.Groups[1].Value.TrimEnd('\\').ToLowerInvariant();
        if (text.StartsWith("Наработка после последнего запуска", StringComparison.OrdinalIgnoreCase)) return "uptime";
        if (text.StartsWith("Windows ниже версии", StringComparison.OrdinalIgnoreCase)) return "windows:outdated";
        if (text.StartsWith("Произошла критическая ошибка Windows", StringComparison.OrdinalIgnoreCase))
            return "bugcheck:" + FindingBugcheck.Match(text).Groups[1].Value;
        if (text.StartsWith("CPU:", StringComparison.OrdinalIgnoreCase))
            return text.Contains("температур", StringComparison.OrdinalIgnoreCase) ? "cpu:temperature" : "cpu:load";
        if (text.StartsWith("ОЗУ:", StringComparison.OrdinalIgnoreCase))
            return text.Contains("менее 500", StringComparison.OrdinalIgnoreCase) ? "memory:low" :
                text.Contains("4 ГБ", StringComparison.OrdinalIgnoreCase) ? "memory:small" : "memory:high";
        if (text.StartsWith("SMART ", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("Windows: тревожное состояние", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("Наработка ", StringComparison.OrdinalIgnoreCase))
            return text.Split(':', 2)[0].Trim().ToLowerInvariant();
        return text.ToLowerInvariant();
    }

    private static MonitorIssue ClientIssue(string title, string detail, short severity)
    {
        var isEvent = title.StartsWith("Произошла критическая ошибка Windows", StringComparison.OrdinalIgnoreCase) ||
            title is "В журнале Windows найдены критические события" or "Windows завершила работу некорректно";
        var code = isEvent ? FindingBugcheck.Match(detail).Groups[1].Value : "";
        return new MonitorIssue(isEvent ? "event" : "finding", title + (code.Length > 0 ? ":" + code : ""), title, detail,
            IsResourcePressure(title, detail) ? (short)2 : severity);
    }

    private static JsonElement? EventArray(JsonElement report)
    {
        var full = JsonRead.Property(report, "Full");
        var events = full is { ValueKind: JsonValueKind.Object } ? JsonRead.Property(full.Value, "Events") : null;
        return events is { ValueKind: JsonValueKind.Array } ? events : JsonRead.Property(report, "Events");
    }

    private static int Level(JsonElement item)
    {
        var raw = JsonRead.String(item, "Level");
        return raw is null ? 2 : int.TryParse(raw, out var level) ? level : 0;
    }

    private static bool IsKernelPower41(JsonElement item) =>
        JsonRead.String(item, "Id") == "41" &&
        (JsonRead.String(item, "Provider", "Source") ?? "").Contains("Kernel-Power", StringComparison.OrdinalIgnoreCase);

    private static bool IsZeroPowerLoss(JsonElement item) => IsKernelPower41(item) &&
        BugcheckCode.Match(Clip(JsonRead.String(item, "Message") ?? "", 2048)).Groups[1].Value == "0";

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}
