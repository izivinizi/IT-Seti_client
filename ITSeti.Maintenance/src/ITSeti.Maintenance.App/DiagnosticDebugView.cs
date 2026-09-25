using System.IO;
using ITSeti.Maintenance.Core;

namespace ITSeti.Maintenance.App;

public sealed record DebugStep(string Name, string State, string Detail);
public sealed record DebugFile(string Name, string Path);

public sealed class DiagnosticDebugView
{
    public string Source { get; private set; } = "Проверка ещё не выполнялась";
    public string Stage { get; private set; } = "—";
    public string Error { get; private set; } = "Ошибок запуска не обнаружено";
    public string DirectoryPath { get; private set; } = "";
    public List<DebugStep> Steps { get; } = [];
    public List<DebugFile> Files { get; } = [];

    public void Refresh(DiagnosticSnapshot? snapshot, string? currentError = null, string? currentStage = null, bool preferSelected = false)
    {
        Steps.Clear();
        Files.Clear();
        var selectedRoot = snapshot?.Full?.ReportDirectory;
        var failedRoot = preferSelected ? null : FindRecentFailedRun(snapshot);
        DirectoryPath = failedRoot ?? (currentError is null && Directory.Exists(selectedRoot) ? selectedRoot! : "");
        Source = failedRoot is not null ? "Последняя незавершённая проверка"
            : currentError is not null ? "Последняя проверка завершилась с ошибкой"
            : snapshot is not null ? $"{snapshot.KindLabel} проверка от {snapshot.DateLabel}"
            : "Проверка ещё не выполнялась";
        Stage = ReadSmallFile("stage.txt") ?? currentStage ?? "—";
        Error = ReadSmallFile("error.txt") ?? currentError ?? "Ошибок запуска не обнаружено";

        if (failedRoot is not null)
        {
            Steps.Add(new("Запуск проверки", "Ошибка", Error));
        }
        else if (currentError is not null) Steps.Add(new("Запуск проверки", "Ошибка", currentError));
        else if (snapshot?.Full is { } full)
        {
            Steps.Add(new("Запуск проверки", "Готово", full.Elevated ? "Проверка с правами администратора" : "Проверка от текущей учётной записи"));
            var sample = full.ResourceSampling;
            Steps.Add(new("Нагрузка CPU и ОЗУ", sample is { Samples: >= 6, Error: "" } ? "Готово" : "Не получено",
                sample?.Error is { Length: > 0 } sampleError ? sampleError : sample is null ? Note(snapshot, "Замер нагрузки:") ?? "Нет результата замера" : $"Получено {sample.Samples} отсчётов"));
            Steps.Add(new("Состояние дисков (SMART)", full.SmartDisks.Count > 0 ? "Готово" : "Не получено",
                full.SmartDisks.Count > 0 ? $"Получено для {full.SmartDisks.Count} накопителей" : Note(snapshot, "SMART:") ?? "CrystalDiskInfo не вернул данные"));
            var benchmark = full.Benchmark;
            Steps.Add(new("Скорость диска", benchmark.State == "Completed" ? "Готово" : benchmark.State == "Running" ? "Выполняется" : "Ошибка",
                benchmark.State == "Completed" ? $"{benchmark.Drive}: чтение {benchmark.ReadLabel}, запись {benchmark.WriteLabel}" : string.IsNullOrWhiteSpace(benchmark.Error) ? "Результат теста не получен" : benchmark.Error));
            Steps.Add(new("Процессы", full.ProcessUnavailable == 0 ? "Готово" : "Частично",
                $"Вне списка: {full.Processes.Count}; без доступа к файлу: {full.ProcessUnavailable}"));
            Steps.Add(new("События Windows", full.EventLimited || full.EventUnavailable > 0 ? "Частично" : "Готово",
                $"Получено: {full.Events.Count}; недоступно: {full.EventUnavailable}"));
            var repair = ReadSmallFile(Path.Combine("repair", "status.txt"));
            var repairStarted = File.Exists(Path.Combine(DirectoryPath, "repair", "started.txt"));
            Steps.Add(new("Восстановление Windows", repair is not null ? "Статус" : repairStarted ? "Запущено" : "Не запускалось",
                repair ?? (repairStarted ? "DISM и SFC выполняются отдельно от диагностики" : "Для проверки пользователя не требуется")));
            foreach (var note in snapshot.Notes.Where(n => n.Contains(':') && !n.StartsWith("Проверка выполнена", StringComparison.OrdinalIgnoreCase)))
                Steps.Add(new("Примечание", "Информация", note));
        }

        if (snapshot is not null)
        {
            var temperature = snapshot.CpuTemperatureC ?? snapshot.Full?.CpuTemperatureC;
            Steps.Add(new("Температура CPU", temperature is null ? "Не получено" : "Готово",
                temperature is null ? snapshot.CpuTemperatureStatus ?? "Источник температуры не проверялся в этой версии" : $"{temperature:N0} °C · {snapshot.CpuTemperatureStatus ?? "источник датчика не указан"}"));
        }

        foreach (var name in new[] { "error.txt", "full-check.log", "disk-worker.log", "resource-sampler.err.txt", "disk-result.xml", "cpu-temperature.json", "result.json", "stage.txt", Path.Combine("repair", "status.txt") })
        {
            var path = Path.Combine(DirectoryPath, name);
            if (DirectoryPath.Length > 0 && File.Exists(path)) Files.Add(new(name, path));
        }
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
