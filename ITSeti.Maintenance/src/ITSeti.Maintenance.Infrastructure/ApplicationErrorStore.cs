using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ITSeti.Maintenance.Infrastructure;

public sealed record ApplicationErrorEntry(
    Guid Id,
    DateTimeOffset OccurredAt,
    string Component,
    string Operation,
    string Summary,
    string ExceptionType,
    string TechnicalDetails,
    string Version);

public static class ApplicationErrorStore
{
    private const int MaximumEntries = 500;
    private static readonly object Gate = new();
    private static readonly Dictionary<string, ApplicationErrorEntry> RecentDuplicates = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetEnvironmentVariable("ITSETI_ERROR_LOG_ROOT") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ITSeti", "Maintenance"), "Logs");
    public static readonly string JsonLogPath = Path.Combine(DirectoryPath, "application-errors.jsonl");
    public static readonly string TextLogPath = Path.Combine(DirectoryPath, "application-errors.log");
    public static readonly string ServerOutboxPath = Path.Combine(DirectoryPath, "server-outbox");
    private static readonly string ReportMarkerPath = Path.Combine(DirectoryPath, "application-errors-report-marker.txt");

    public static ApplicationErrorEntry Record(string component, string operation, Exception exception, string? summary = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var entry = new ApplicationErrorEntry(Guid.NewGuid(), DateTimeOffset.UtcNow,
            Clean(component, 80), Clean(operation, 160),
            Clean(summary ?? FriendlySummary(exception), 500), exception.GetType().FullName ?? exception.GetType().Name,
            Redact(exception.ToString(), 16000),
            Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "неизвестно");
        try
        {
            lock (Gate)
            {
                var key = entry.Component + "|" + entry.Operation + "|" + entry.Summary;
                if (RecentDuplicates.TryGetValue(key, out var prior) && entry.OccurredAt - prior.OccurredAt < TimeSpan.FromMinutes(5)) return prior;
                foreach (var expired in RecentDuplicates.Where(item => entry.OccurredAt - item.Value.OccurredAt >= TimeSpan.FromMinutes(5)).Select(item => item.Key).ToArray()) RecentDuplicates.Remove(expired);
                Directory.CreateDirectory(DirectoryPath);
                File.AppendAllText(JsonLogPath, JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine, new UTF8Encoding(false));
                File.AppendAllText(TextLogPath,
                    $"[{entry.OccurredAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}] {entry.Id:N} | {entry.Component} | {entry.Operation} | {entry.Summary}\r\n{entry.TechnicalDetails}\r\n\r\n",
                    new UTF8Encoding(false));
                QueueForServer(entry);
                TrimIfNeeded();
                RecentDuplicates[key] = entry;
            }
        }
        catch (Exception logException) when (logException is IOException or UnauthorizedAccessException or JsonException)
        {
            System.Diagnostics.Debug.WriteLine("Application error log failed: " + logException.Message);
        }
        return entry;
    }

    public static IReadOnlyList<ApplicationErrorEntry> ReadRecent(int limit = 100)
    {
        if (limit <= 0 || !File.Exists(JsonLogPath)) return [];
        try
        {
            lock (Gate)
            {
                return File.ReadLines(JsonLogPath)
                    .Select(TryDeserialize).Where(entry => entry is not null).Cast<ApplicationErrorEntry>()
                    .TakeLast(Math.Min(limit, MaximumEntries)).Reverse().ToArray();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    public static IReadOnlyList<ApplicationErrorEntry> ReadUnreported(int limit = 20)
    {
        var marker = DateTimeOffset.MinValue;
        try
        {
            if (File.Exists(ReportMarkerPath)) DateTimeOffset.TryParse(File.ReadAllText(ReportMarkerPath), out marker);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return ReadRecent(MaximumEntries).Where(entry => entry.OccurredAt > marker)
            .OrderBy(entry => entry.OccurredAt).Take(Math.Max(1, limit)).ToArray();
    }

    public static void MarkReported(IReadOnlyCollection<ApplicationErrorEntry> entries)
    {
        if (entries.Count == 0) return;
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            File.WriteAllText(ReportMarkerPath, entries.Max(entry => entry.OccurredAt).ToString("O"), new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public static string UserMessage(ApplicationErrorEntry entry) =>
        $"{entry.Summary}\n\nКод события: {entry.Id:N}. Подробности сохранены во вкладке «Отладка».";

    private static ApplicationErrorEntry? TryDeserialize(string line)
    {
        try { return JsonSerializer.Deserialize<ApplicationErrorEntry>(line, JsonOptions); }
        catch (JsonException) { return null; }
    }

    private static void QueueForServer(ApplicationErrorEntry entry)
    {
        Directory.CreateDirectory(ServerOutboxPath);
        var target = Path.Combine(ServerOutboxPath, entry.Id.ToString("N") + ".json");
        var temporary = target + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(entry, JsonOptions), new UTF8Encoding(false));
            File.Move(temporary, target, true);
        }
        finally
        {
            try { File.Delete(temporary); } catch (IOException) { }
        }
    }

    private static string FriendlySummary(Exception exception) => exception switch
    {
        OperationCanceledException => "Операция была отменена.",
        UnauthorizedAccessException access => "Windows запретила доступ: " + access.Message +
            " Проверьте права на указанную папку и системную службу обслуживания.",
        FileNotFoundException file => "Не найден обязательный файл" + (string.IsNullOrWhiteSpace(file.FileName) ? "." : $": {Path.GetFileName(file.FileName)}."),
        DirectoryNotFoundException => "Не найдена обязательная папка приложения.",
        System.Net.Http.HttpRequestException => "Не удалось получить ответ сервера.",
        TimeoutException => "Операция не завершилась за отведённое время.",
        JsonException => "Получены повреждённые или несовместимые данные.",
        System.ComponentModel.Win32Exception win32 => "Windows не выполнила операцию" + (string.IsNullOrWhiteSpace(win32.Message) ? "." : $": {win32.Message}"),
        _ => string.IsNullOrWhiteSpace(exception.Message) ? "Возникла внутренняя ошибка приложения." : exception.Message
    };

    private static string Clean(string value, int max) => Redact(value.ReplaceLineEndings(" ").Trim(), max);

    private static string Redact(string value, int max)
    {
        var sanitized = Regex.Replace(value, @"(?i)\bBearer\s+[A-Za-z0-9._~+/=-]+", "Bearer <скрыто>");
        sanitized = Regex.Replace(sanitized, "(?i)(password|пароль|authorization|device[-_ ]?key|token)[\\\"']?\\s*[:=]\\s*(?:\\\"[^\\\"]*\\\"|'[^']*'|[^\\s,;]+)", "$1=<скрыто>");
        return sanitized.Length <= max ? sanitized : sanitized[..max] + "…";
    }

    private static void TrimIfNeeded()
    {
        var file = new FileInfo(JsonLogPath);
        if (!file.Exists || file.Length < 2 * 1024 * 1024) return;
        var lines = File.ReadLines(JsonLogPath).TakeLast(MaximumEntries).ToArray();
        File.WriteAllLines(JsonLogPath, lines, new UTF8Encoding(false));
        var text = lines.Select(TryDeserialize).Where(entry => entry is not null).Select(entry =>
            $"[{entry!.OccurredAt:O}] {entry.Id:N} | {entry.Component} | {entry.Operation} | {entry.Summary}\r\n{entry.TechnicalDetails}\r\n");
        File.WriteAllLines(TextLogPath, text, new UTF8Encoding(false));
    }
}
