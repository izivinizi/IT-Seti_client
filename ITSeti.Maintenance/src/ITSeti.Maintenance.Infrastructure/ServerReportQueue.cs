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
        if (!Directory.Exists(QueueDirectory)) return;
        var target = Path.Combine(QueueDirectory, snapshot.Id.ToString("N") + ".json");
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var report = JsonSerializer.SerializeToNode(snapshot)!.AsObject();
            report["Findings"] = JsonSerializer.SerializeToNode(DiagnosticRules.GetFindings(snapshot));
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
            using var process = Process.Start(new ProcessStartInfo("schtasks.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList = { "/Run", "/TN", "ITSeti-Maintenance-Upload" }
            });
            if (process is not null) await process.WaitForExitAsync();
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception) { }
    }
}
