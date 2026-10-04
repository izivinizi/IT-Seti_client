using System.Text.Json;
using System.Text.RegularExpressions;
using ITSeti.Maintenance.Core;

namespace ITSeti.Server;

public sealed record MonitorIssue(string Category, string Identity, string Title, string Detail, short Severity);

public static class Monitoring
{
    public static IReadOnlyList<MonitorIssue> CurrentIssues(JsonElement report) => Extract(report);
    private static readonly Regex SpaceNote = new(@"^([A-Z]:\\?)\s+свободно\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex SpaceAmount = new(@"\bсвободно\s+([0-9]+(?:[.,][0-9]+)?)\s*ГБ", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex BugcheckCode = new(@"\bBugcheckCode\s*[:=]\s*(\d+)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex FindingBugcheck = new(@"\bКод\s+(\d+)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public static IReadOnlyList<MonitorIssue> NewIssues(JsonElement current, JsonElement? previous, JsonElement? previousEvents = null, bool includeWarnings = false)
    {
        if (previous is null) return [];
        var currentIssues = Extract(current);
        var eventBaseline = previousEvents ?? previous;
        var oldIssues = Extract(previous.Value).Where(i => i.Category != "event")
            .Concat(eventBaseline is null ? [] : Extract(eventBaseline.Value).Where(i => i.Category == "event"));
        var oldKeys = oldIssues.Select(i => i.Category + "|" + i.Identity + "|" + i.Severity).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hadEventBaseline = eventBaseline is not null && EventArray(eventBaseline.Value) is { ValueKind: JsonValueKind.Array };
        return currentIssues.Where(i => (i.Severity == 1 || includeWarnings) && (i.Category != "event" || hadEventBaseline) &&
            !oldKeys.Contains(i.Category + "|" + i.Identity + "|" + i.Severity)).ToArray();
    }

    private static IReadOnlyList<MonitorIssue> Extract(JsonElement report)
    {
        var issues = new Dictionary<string, MonitorIssue>(StringComparer.OrdinalIgnoreCase);
        var clientIssues = JsonRead.Property(report, "DiagnosticIssues");
        if (clientIssues is { ValueKind: JsonValueKind.Array })
            return clientIssues.Value.EnumerateArray().Where(i => JsonRead.String(i, "Severity") is "Critical" or "Warning")
                .Select(i => ClientIssue(JsonRead.String(i, "Title") ?? "", JsonRead.String(i, "Detail") ?? "",
                    JsonRead.String(i, "Severity") == "Critical" ? (short)1 : (short)2)).ToArray();
        if (JsonRead.Property(report, "Notes") is { ValueKind: JsonValueKind.Array } &&
            JsonRead.Property(report, "Disks") is { ValueKind: JsonValueKind.Array })
        {
            try
            {
                var snapshot = report.Deserialize<DiagnosticSnapshot>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (snapshot is not null)
                    return DiagnosticRules.GetUserIssues(snapshot).Select(i => ClientIssue(i.Title, i.Detail,
                        i.Severity == "Critical" ? (short)1 : (short)2)).ToArray();
            }
            catch (Exception ex) when (ex is JsonException or NullReferenceException or ArgumentException) { }
        }
        foreach (var note in JsonRead.StringList(report, "Notes").Concat(JsonRead.StringList(report, "Findings")).Distinct())
        {
            var text = Clip(note.Trim(), 2000);
            if (!Regex.IsMatch(text, @"^(CPU:|ОЗУ:|SMART[ :]|Windows: тревожное|Windows ниже версии|Наработка |[A-Z]:\\?\s+свободно|Произошла критическая ошибка Windows)", RegexOptions.IgnoreCase)) continue;
            if (text.Length == 0 || text.StartsWith("Критических событий в доступной выборке:", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("Проверка выполнена", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("Ограничение интерфейса", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("HDD, не SSD", StringComparison.OrdinalIgnoreCase)) continue;
            var identity = NoteIdentity(text);
            var space = SpaceAmount.Match(text);
            var criticalSpace = space.Success && double.TryParse(space.Groups[1].Value.Replace(',', '.'),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var freeGb) && freeGb < 5;
            var severity = criticalSpace || text.Contains("критически", StringComparison.OrdinalIgnoreCase) ||
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
        return new MonitorIssue(isEvent ? "event" : "finding", title + (code.Length > 0 ? ":" + code : ""), title, detail, severity);
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
