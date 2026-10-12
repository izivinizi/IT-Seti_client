using System.Text.Json;
using ITSeti.Maintenance.Core;

namespace ITSeti.Maintenance.Infrastructure;

public sealed record InstalledDiagnosticReport(
    string DirectoryName,
    DateTimeOffset UpdatedAtUtc,
    DiagnosticSnapshot Snapshot);

public sealed class InstalledReportReader
{
    private const int InitialBackfillLimit = 100;
    private const int DefaultBatchSize = 20;
    private readonly string runsDirectory;
    private readonly HashSet<string> importedDirectories = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<(string Name, long LastWriteTicks)> failedVersions = [];
    private ReportFile[] initialBackfill = [];
    private bool initialBackfillLoaded;
    private bool initialBackfillComplete;
    private DateTimeOffset? importedThroughUtc;

    public InstalledReportReader(string? runsDirectory = null)
    {
        this.runsDirectory = Path.GetFullPath(runsDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "ITSeti", "Maintenance", "Runs"));
    }

    public async Task<IReadOnlyList<InstalledDiagnosticReport>> ReadBatchAsync(
        int batchSize = DefaultBatchSize, CancellationToken cancellationToken = default)
    {
        if (batchSize < 1) throw new ArgumentOutOfRangeException(nameof(batchSize));
        if (!Directory.Exists(runsDirectory)) return [];

        if (!initialBackfillLoaded)
        {
            initialBackfill = await Task.Run(() => DiscoverFiles()
                    .OrderByDescending(file => file.UpdatedAtUtc)
                    .Take(InitialBackfillLimit)
                    .OrderBy(file => file.UpdatedAtUtc)
                    .ToArray(), cancellationToken)
                .ConfigureAwait(false);
            initialBackfillLoaded = true;
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidates = initialBackfillComplete
                ? await Task.Run(() => DiscoverFiles()
                        .Where(IsPending)
                        .Where(file => importedThroughUtc is null || file.UpdatedAtUtc >= importedThroughUtc.Value)
                        .OrderBy(file => file.UpdatedAtUtc)
                        .Take(batchSize)
                        .ToArray(), cancellationToken)
                    .ConfigureAwait(false)
                : initialBackfill.Where(IsPending).Take(batchSize).ToArray();

            if (candidates.Length == 0)
            {
                if (!initialBackfillComplete)
                {
                    initialBackfillComplete = true;
                    if (initialBackfill.Length > 0)
                    {
                        var newestBackfill = initialBackfill.Max(file => file.UpdatedAtUtc);
                        if (importedThroughUtc is null || newestBackfill > importedThroughUtc.Value)
                            importedThroughUtc = newestBackfill;
                    }
                    continue;
                }
                return [];
            }

            var reports = new List<InstalledDiagnosticReport>(candidates.Length);
            foreach (var file in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var snapshot = await FullDiagnosticsRunner.ReadSnapshot(file.Path, cancellationToken).ConfigureAwait(false);
                    reports.Add(new(file.Name, file.UpdatedAtUtc, snapshot));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                    or InvalidDataException or ArgumentException)
                {
                    failedVersions.Add((file.Name, file.LastWriteTicks));
                    ApplicationErrorStore.Record("История", "Чтение системного отчёта", ex,
                        "Не удалось прочитать системный отчёт " + file.Name + ".");
                }
            }

            if (reports.Count > 0) return reports;
        }
    }

    public void MarkImported(IReadOnlyCollection<InstalledDiagnosticReport> reports)
    {
        foreach (var report in reports)
        {
            importedDirectories.Add(report.DirectoryName);
            if (importedThroughUtc is null || report.UpdatedAtUtc > importedThroughUtc.Value)
                importedThroughUtc = report.UpdatedAtUtc;
        }
    }

    private bool IsPending(ReportFile file) =>
        !importedDirectories.Contains(file.Name) && !failedVersions.Contains((file.Name, file.LastWriteTicks));

    private ReportFile[] DiscoverFiles()
    {
        var files = new List<ReportFile>();
        foreach (var directory in Directory.EnumerateDirectories(runsDirectory))
        {
            var path = Path.Combine(directory, "result.json");
            var info = new FileInfo(path);
            if (!info.Exists) continue;
            var updatedAt = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
            files.Add(new(Path.GetFileName(directory), path, updatedAt, info.LastWriteTimeUtc.Ticks));
        }
        return files.ToArray();
    }

    private sealed record ReportFile(string Name, string Path, DateTimeOffset UpdatedAtUtc, long LastWriteTicks);
}
