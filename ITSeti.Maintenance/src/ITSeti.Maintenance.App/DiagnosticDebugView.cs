using System.IO;
using ITSeti.Maintenance.Core;
using ITSeti.Maintenance.Infrastructure;

namespace ITSeti.Maintenance.App;

public sealed record DebugStep(string Name, string State, string Detail);
public sealed record DebugFile(string Name, string Path);

public sealed class DiagnosticDebugView
{
    public string Source { get; private set; } = "Ошибок приложения не зарегистрировано";
    public string Stage { get; private set; } = "—";
    public string Error { get; private set; } = "Ошибок запуска не обнаружено";
    public string DirectoryPath { get; private set; } = "";
    public List<DebugStep> Steps { get; } = [];
    public List<DebugFile> Files { get; } = [];

    public void Refresh(DiagnosticSnapshot? snapshot, string? currentError = null, string? currentStage = null, bool preferSelected = false)
    {
        Steps.Clear();
        Files.Clear();
        DirectoryPath = Path.GetDirectoryName(ApplicationErrorStore.JsonLogPath) ?? "";
        var errors = ApplicationErrorStore.ReadRecent(100);
        Source = errors.Count == 0 ? "Ошибок приложения не зарегистрировано" : $"Зарегистрировано ошибок: {errors.Count}";
        Stage = errors.Count == 0 ? "—" : errors[0].OccurredAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss");
        Error = errors.Count == 0 ? "Ошибок запуска не обнаружено" : errors[0].Summary;
        foreach (var error in errors)
            Steps.Add(new($"{error.OccurredAt.ToLocalTime():dd.MM HH:mm:ss} · {error.Component}", error.Operation,
                $"{error.Summary}\nКод: {error.Id:N}\n{error.ExceptionType}\n{error.TechnicalDetails}"));
        if (File.Exists(ApplicationErrorStore.TextLogPath)) Files.Add(new("application-errors.log", ApplicationErrorStore.TextLogPath));
        if (File.Exists(ApplicationErrorStore.JsonLogPath)) Files.Add(new("application-errors.jsonl", ApplicationErrorStore.JsonLogPath));
    }

    private string? ReadSmallFile(string relative)
    {
        if (DirectoryPath.Length == 0) return null;
        try
        {
            var path = Path.Combine(DirectoryPath, relative);
            if (!File.Exists(path)) return null;
            using var reader = new StreamReader(path);
            var buffer = new char[4096];
            var count = reader.Read(buffer, 0, buffer.Length);
            return new string(buffer, 0, count).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "Файл недоступен: " + ex.Message; }
    }

    private static string? Note(DiagnosticSnapshot snapshot, string prefix) =>
        snapshot.Notes.FirstOrDefault(note => note.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    private static string? FindRecentFailedRun(DiagnosticSnapshot? snapshot)
    {
        var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ITSeti", "Maintenance");
        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ITSeti", "Maintenance", "Runs");
        var candidates = new List<string>();
        try
        {
            var latest = Path.Combine(installed, "latest.txt");
            if (File.Exists(latest))
            {
                var root = Path.GetFullPath(File.ReadAllText(latest).Trim());
                if (root.StartsWith(Path.Combine(installed, "Runs") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) candidates.Add(root);
            }
            if (Directory.Exists(local)) candidates.AddRange(Directory.GetDirectories(local).OrderByDescending(Directory.GetCreationTimeUtc).Take(1));
            return candidates.Where(Directory.Exists)
                .Where(root => File.Exists(Path.Combine(root, "error.txt")) && !File.Exists(Path.Combine(root, "result.json")))
                .Where(root => snapshot is null || Directory.GetCreationTimeUtc(root) > snapshot.StartedAt.UtcDateTime)
                .OrderByDescending(Directory.GetCreationTimeUtc).FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }
}
