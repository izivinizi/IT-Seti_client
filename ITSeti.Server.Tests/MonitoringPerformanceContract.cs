using System.Diagnostics;
using System.Text.Json;
using ITSeti.Maintenance.Core;
using ITSeti.Server;

internal static class MonitoringPerformanceContract
{
    public static void Run()
    {
        var snapshot = new DiagnosticSnapshot(Guid.NewGuid(), DateTimeOffset.UtcNow, "PERFORMANCE-FIXTURE", 20,
            16UL << 30, 8UL << 30, [new DiskSnapshot("C:", 500L << 30, 100L << 30)], [],
            CpuTemperatureC: 90,
            HwInfo: new HwInfoSnapshot("fixture", "8.54", DateTimeOffset.UtcNow, null, null, null, null,
                Enumerable.Range(0, 40).Select(i => new HwInfoSensorReading("CPU Core " + i, 50 + i % 8, "°C", "CpuTemperature")).ToArray()));
        var report = JsonSerializer.SerializeToNode(snapshot)!.AsObject();
        report["DiagnosticResult"] = JsonSerializer.SerializeToNode(DiagnosticEngine.Evaluate(snapshot));
        using var document = JsonDocument.Parse(report.ToJsonString());
        var current = document.RootElement;
        void RunOnce()
        {
            var issues = Monitoring.CurrentIssues(current);
            if (issues.Count == 0 || Monitoring.NewIssues(current, current, currentIssues: issues).Count != 0)
                throw new Exception("Unchanged reports must retain issues but suppress repeated alerts.");
        }
        for (var i = 0; i < 30; i++) RunOnce();
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var timer = Stopwatch.StartNew();
        for (var i = 0; i < 300; i++) RunOnce();
        timer.Stop();
        Console.WriteLine(JsonSerializer.Serialize(new { benchmark = "monitoring-report", iterations = 300,
            elapsedMs = timer.Elapsed.TotalMilliseconds, allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated }));
        Console.WriteLine("PASS raw-input evaluation and unchanged-report alert suppression");
    }
}
