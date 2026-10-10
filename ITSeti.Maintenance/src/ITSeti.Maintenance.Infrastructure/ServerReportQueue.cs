using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ITSeti.Maintenance.Core;

namespace ITSeti.Maintenance.Infrastructure;

public static class ServerReportQueue
{
    private static readonly string QueueDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ITSeti", "Maintenance", "ReportQueue");

    public static async Task EnqueueAsync(DiagnosticSnapshot snapshot)
    {
        if (Environment.GetEnvironmentVariable("ITSETI_DISABLE_REPORT_UPLOAD") == "1") return;
        Directory.CreateDirectory(QueueDirectory);
        var target = Path.Combine(QueueDirectory, snapshot.Id.ToString("N") + ".json");
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var report = JsonSerializer.SerializeToNode(snapshot)!.AsObject();
            report["Notes"] = JsonSerializer.SerializeToNode(snapshot.Notes.Where(note =>
                !note.StartsWith("Ограничение интерфейса", StringComparison.OrdinalIgnoreCase) &&
                !note.StartsWith("Без доступа к исполняемому файлу процессов", StringComparison.OrdinalIgnoreCase)));
            report["Findings"] = JsonSerializer.SerializeToNode(DiagnosticRules.GetFindings(snapshot, includeDiskLinkWarnings: false));
            report["DiagnosticIssues"] = JsonSerializer.SerializeToNode(DiagnosticRules.GetUserIssues(snapshot));
            report["KindLabel"] = snapshot.KindLabel;
            report["MemoryUsedPercent"] = snapshot.MemoryUsedPercent;
            await File.WriteAllTextAsync(temporary, report.ToJsonString());
            File.Move(temporary, target, true);
        }
        finally
        {
            try { File.Delete(temporary); } catch (IOException) { }
        }
        try
        {
            var service = await MaintenanceServiceClient.StartAsync(MaintenanceServiceOperation.UploadReports);
            if (service.Connected)
            {
                if (!service.Accepted) throw new InvalidOperationException(service.Message);
                return;
            }
            using var process = Process.Start(new ProcessStartInfo("schtasks.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList = { "/Run", "/TN", "ITSeti-Maintenance-Upload" }
            });
            if (process is null) throw new InvalidOperationException("Не удалось запустить задачу отправки отчётов.");
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new InvalidOperationException($"Задача отправки отчётов завершилась с кодом {process.ExitCode}.");
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            var errorPath = Path.Combine(QueueDirectory, "server-upload-error.txt");
            try { await File.WriteAllTextAsync(errorPath, ex.Message); } catch (IOException) { }
        }
    }
}
