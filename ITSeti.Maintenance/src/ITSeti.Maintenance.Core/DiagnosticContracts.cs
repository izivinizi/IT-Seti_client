namespace ITSeti.Maintenance.Core;

public sealed record DiagnosticProgress(string Stage, DiagnosticSnapshot? Snapshot = null, IReadOnlyList<string>? Messages = null);
public interface IFullDiagnosticsRunner
{
    Task<DiagnosticSnapshot> RunAsync(IProgress<DiagnosticProgress> progress, CancellationToken cancellationToken = default);
    Task<DiagnosticSnapshot> RunUserAsync(IProgress<DiagnosticProgress> progress, CancellationToken cancellationToken = default);
    Task<DiagnosticSnapshot> RunQuickAsync(IProgress<DiagnosticProgress> progress, CancellationToken cancellationToken = default);
    Task<DiagnosticSnapshot> RunUserQuickAsync(IProgress<DiagnosticProgress> progress, CancellationToken cancellationToken = default);
}

public interface IDiagnosticsRunner
{
    Task<DiagnosticSnapshot> RunAsync(CancellationToken cancellationToken = default);
}

public interface IHistoryStore
{
    Task SaveAsync(DiagnosticSnapshot snapshot);
    Task SaveManyAsync(IReadOnlyCollection<DiagnosticSnapshot> snapshots);
    Task<IReadOnlyList<DiagnosticSnapshot>> GetRecentAsync(int limit = 25, int offset = 0);
}
