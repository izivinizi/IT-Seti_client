using System.IO;
using System.Text.Json;
using ITSeti.Maintenance.Core;
using ITSeti.Maintenance.Infrastructure;

internal static class RefactorContract
{
    internal static int Run()
    {
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        var queue = new OperationQueue<string>();
        queue.Enqueue("full", "diagnostics");
        queue.Enqueue("smart", "diagnostics");
        Check(queue.TryStart("full") && !queue.TryStart("smart"), "Overlapping diagnostics");
        Check(!queue.Enqueue("full", "diagnostics"), "Duplicate operation accepted");
        queue.Enqueue("upload", "software");
        Check(queue.TryStart("upload"), "Independent operation blocked");
        queue.Cancel("full");
        Check(!queue.TryStart("smart"), "Cancelling released group before worker exit");
        queue.Complete("full", "killed");
        Check(queue.Snapshot.Single(x => x.Operation == "full").State == OperationState.Cancelled && queue.TryStart("smart"), "Cancel/next transition failed");
        queue.Complete("smart", "test failure");
        queue.Enqueue("smart", "diagnostics", retry: true);
        Check(queue.TryStart("smart") && queue.Snapshot.Single(x => x.Operation == "smart").Attempts == 2, "Retry attempts lost");
        queue.Complete("smart");
        queue.Enqueue("waiting", "software");
        queue.Cancel("waiting");
        Check(!queue.Pending.Contains("waiting"), "Cancelled queued operation survived");
        var parallel = new OperationQueue<string>();
        Parallel.For(0, 100, _ => parallel.Enqueue("full", "diagnostics"));
        Check(parallel.Pending.Count == 1, "Concurrent duplicate queue entries");

        Check(!HwInfoRecoveryPolicy.ShouldRestart(5, DateTimeOffset.UtcNow, DateTimeOffset.MinValue) &&
            HwInfoRecoveryPolicy.ShouldRestart(6, DateTimeOffset.UtcNow, DateTimeOffset.MinValue) &&
            !HwInfoRecoveryPolicy.ShouldRestart(12, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1)) &&
            !HwInfoRecoveryPolicy.IsSourceTimestampStale(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow) &&
            HwInfoRecoveryPolicy.IsSourceTimestampStale(DateTimeOffset.UtcNow.AddMinutes(-2), DateTimeOffset.UtcNow) &&
            HwInfoRecoveryPolicy.IsSourceTimestampStale(DateTimeOffset.MinValue, DateTimeOffset.UtcNow) &&
            !HwInfoRecoveryPolicy.ShouldRestartStaleSource(5, DateTimeOffset.UtcNow, DateTimeOffset.MinValue) &&
            HwInfoRecoveryPolicy.ShouldRestartStaleSource(6, DateTimeOffset.UtcNow, DateTimeOffset.MinValue) &&
            !HwInfoRecoveryPolicy.ShouldRestartStaleSource(12, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1)),
            "Missing-map recovery cadence is unsafe");
        var monitoringStart = new DateTimeOffset(2026, 10, 14, 0, 0, 0, TimeSpan.Zero);
        var latencySnapshot = new DiagnosticSnapshot(Guid.NewGuid(), monitoringStart, "fixture", 0, 0, 0, [], [],
            DiskReadLatencyMs: 100);
        var latencyIssue = CriticalMonitoringEvaluator.Evaluate(latencySnapshot);
        Check(latencyIssue.Count == 1 && latencyIssue[0].Identity == "disk:latency", "100 ms disk response did not enter critical monitoring");
        var confirmation = new MonitoringConfirmation();
        for (var sample = 0; sample < 6; sample++)
            Check(confirmation.Observe(latencyIssue, monitoringStart.AddSeconds(sample * 5)).Count == 0,
                "Disk response alert fired before 30 seconds");
        Check(confirmation.Observe(latencyIssue, monitoringStart.AddSeconds(30)).Count == 1 &&
            confirmation.Observe(latencyIssue, monitoringStart.AddSeconds(35)).Count == 0,
            "Disk response alert did not fire once after 30 seconds");
        confirmation.Observe([], monitoringStart.AddSeconds(40));
        Check(confirmation.Observe(latencyIssue, monitoringStart.AddSeconds(45)).Count == 0 &&
            confirmation.Observe(latencyIssue, monitoringStart.AddSeconds(50)).Count == 0,
            "Disk response rearm fired before a fresh 30-second interval");

        var overloaded = new DiagnosticSnapshot(Guid.NewGuid(), monitoringStart, "fixture", 98,
            1000, 20, [], []);
        var loadIssuesAt = (DateTimeOffset capturedAt) => CriticalMonitoringEvaluator.Evaluate(overloaded with
        {
            StartedAt = capturedAt,
            CpuPercentCapturedAtUtc = capturedAt,
            MemoryUsedPercentCapturedAtUtc = capturedAt
        });
        var loadIssues = loadIssuesAt(monitoringStart);
        Check(loadIssues.Any(issue => issue.Identity == "cpu:load") && loadIssues.Any(issue => issue.Identity == "memory:load"),
            "Critical CPU or memory load was not included in continuous monitoring");
        var loadConfirmation = new MonitoringConfirmation();
        loadConfirmation.Observe([], monitoringStart.AddSeconds(-5));
        for (var sample = 0; sample < 7; sample++)
        {
            var capturedAt = monitoringStart.AddSeconds(sample * 5);
            Check(loadConfirmation.Observe(loadIssuesAt(capturedAt), capturedAt).Count == 0,
                "CPU/RAM load alert fired before eight readings within one minute");
        }
        var loadConfirmationAt35 = monitoringStart.AddSeconds(35);
        var confirmedLoads = loadConfirmation.Observe(loadIssuesAt(loadConfirmationAt35), loadConfirmationAt35);
        Check(confirmedLoads.Count == 2 && confirmedLoads.Any(issue => issue.Identity == "cpu:load") &&
            confirmedLoads.Any(issue => issue.Identity == "memory:load"),
            "CPU/RAM load alerts did not fire after eight readings within one minute");
        var staleLoads = CriticalMonitoringEvaluator.Evaluate(overloaded with
        {
            StartedAt = monitoringStart.AddSeconds(20),
            CpuPercentCapturedAtUtc = monitoringStart,
            MemoryUsedPercentCapturedAtUtc = monitoringStart
        });
        Check(staleLoads.All(issue => issue.Identity is not "cpu:load" and not "memory:load"),
            "Cached CPU/RAM readings were treated as new measurements");

        var voltageIssueAt = (DateTimeOffset at) => new[]
        {
            new CriticalMonitoringIssue("voltage:test", "Voltage", "Voltage is out of range", at,
                MonitoringIssueType.Voltage)
        };
        var voltageConfirmation = new MonitoringConfirmation();
        voltageConfirmation.Observe([], monitoringStart.AddSeconds(-5));
        for (var sample = 0; sample < 2; sample++)
        {
            var at = monitoringStart.AddSeconds(sample * 5);
            Check(voltageConfirmation.Observe(voltageIssueAt(at), at).Count == 0,
                "Voltage alert fired before three readings within one minute");
        }
        var voltageAt10 = monitoringStart.AddSeconds(10);
        Check(voltageConfirmation.Observe(voltageIssueAt(voltageAt10), voltageAt10).Count == 1 &&
            voltageConfirmation.Observe(voltageIssueAt(monitoringStart.AddSeconds(15)), monitoringStart.AddSeconds(15)).Count == 0,
            "Voltage alert did not confirm on the third reading exactly once");
        var voltageRecurrence = new MonitoringConfirmation();
        var voltageEvents = new[] { 0, 10, 20 };
        var thirdVoltageConfirmed = false;
        foreach (var seconds in voltageEvents)
        {
            var at = monitoringStart.AddSeconds(seconds);
            var reported = voltageRecurrence.Observe(voltageIssueAt(at), at);
            if (seconds == 20) thirdVoltageConfirmed = reported.Count == 1;
            else Check(reported.Count == 0, "Voltage short event was confirmed before the third occurrence");
            var clearAt = at.AddSeconds(1);
            voltageRecurrence.Observe([], clearAt);
        }
        Check(thirdVoltageConfirmed, "Three short voltage events within an hour did not confirm");

        var voltageWindow = new MonitoringConfirmation();
        foreach (var seconds in new[] { 0, 1800, 3601, 3610 })
        {
            var at = monitoringStart.AddSeconds(seconds);
            var reported = voltageWindow.Observe(voltageIssueAt(at), at);
            if (seconds == 3610)
                Check(reported.Count == 1, "Expired voltage occurrences were not pruned from the one-hour window");
            else
                Check(reported.Count == 0, "Voltage recurrence ignored its one-hour window");
            voltageWindow.Observe([], at.AddSeconds(1));
        }

        var temperatureIssueAt = (DateTimeOffset at) => new[]
        {
            new CriticalMonitoringIssue("temperature:test", "Temperature", "Temperature is too high", at,
                MonitoringIssueType.Temperature)
        };
        var temperatureConfirmation = new MonitoringConfirmation();
        temperatureConfirmation.Observe([], monitoringStart.AddSeconds(-5));
        for (var sample = 0; sample < 5; sample++)
        {
            var at = monitoringStart.AddSeconds(sample * 5);
            Check(temperatureConfirmation.Observe(temperatureIssueAt(at), at).Count == 0,
                "Temperature alert fired before six readings within one minute");
        }
        var temperatureAt25 = monitoringStart.AddSeconds(25);
        Check(temperatureConfirmation.Observe(temperatureIssueAt(temperatureAt25), temperatureAt25).Count == 1,
            "Temperature alert did not confirm on the sixth reading");
        var temperatureRecurrence = new MonitoringConfirmation();
        temperatureRecurrence.Observe([], monitoringStart.AddSeconds(-5));
        for (var sample = 0; sample < 3; sample++)
        {
            var at = monitoringStart.AddSeconds(sample * 5);
            Check(temperatureRecurrence.Observe(temperatureIssueAt(at), at).Count == 0,
                "First short temperature episode was confirmed early");
        }
        temperatureRecurrence.Observe([], monitoringStart.AddSeconds(15));
        for (var sample = 0; sample < 2; sample++)
        {
            var at = monitoringStart.AddSeconds(20 + sample * 5);
            Check(temperatureRecurrence.Observe(temperatureIssueAt(at), at).Count == 0,
                "Second short temperature episode was confirmed before 15 seconds");
        }
        var secondTemperatureEpisodeAt15 = monitoringStart.AddSeconds(30);
        Check(temperatureRecurrence.Observe(temperatureIssueAt(secondTemperatureEpisodeAt15), secondTemperatureEpisodeAt15).Count == 1,
            "Two temperature episodes of at least 15 seconds within an hour did not confirm");

        var sensorConfirmation = new MonitoringConfirmation();
        var sensorIssuesAt = (DateTimeOffset capturedAt) => CriticalMonitoringEvaluator.Evaluate(
            new DiagnosticSnapshot(Guid.NewGuid(), capturedAt, "fixture", 0, 0, 0, [], [],
                HwInfo: new HwInfoSnapshot("fixture", "8.54", capturedAt, null, null, null, null,
                    [new HwInfoSensorReading("Auxiliary alarm", 1, "state", "Other", "critical", IsCritical: true)])));
        Check(sensorIssuesAt(monitoringStart).Single().Severity == "Warning", "Unknown HWiNFO critical sensor was not categorized as a warning");
        for (var sample = 0; sample < 1; sample++)
        {
            var capturedAt = monitoringStart.AddSeconds(sample * 5);
            Check(sensorConfirmation.Observe(sensorIssuesAt(capturedAt), capturedAt).Count == 0,
                "Unknown HWiNFO critical sensor fired before two distinct readings");
        }
        var confirmedSensors = sensorConfirmation.Observe(sensorIssuesAt(monitoringStart.AddSeconds(5)), monitoringStart.AddSeconds(5));
        Check(confirmedSensors.Count == 1 && confirmedSensors[0].Severity == "Warning" &&
            confirmedSensors[0].Identity == "hwinfo:critical:auxiliary alarm" &&
            sensorConfirmation.Observe(sensorIssuesAt(monitoringStart.AddSeconds(10)), monitoringStart.AddSeconds(10)).Count == 0,
            "Unknown HWiNFO critical sensor did not produce one warning after two readings");

        var duplicateConfirmation = new MonitoringConfirmation();
        duplicateConfirmation.Observe([], monitoringStart.AddSeconds(-5));
        var firstCachedLoad = loadIssuesAt(monitoringStart);
        Check(duplicateConfirmation.Observe(firstCachedLoad, monitoringStart).Count == 0, "First resource sample unexpectedly confirmed");
        for (var tick = 1; tick <= 19; tick++)
            Check(duplicateConfirmation.Observe(firstCachedLoad, monitoringStart.AddSeconds(tick * 5)).Count == 0,
                "Repeated cached measurements advanced the confirmation interval");
        duplicateConfirmation.Observe([], monitoringStart.AddSeconds(100));
        for (var sample = 0; sample < 7; sample++)
        {
            var at = monitoringStart.AddSeconds(105 + sample * 5);
            Check(duplicateConfirmation.Observe(loadIssuesAt(at), at).Count == 0,
                "A gapped sequence triggered a resource alert before eight fresh readings");
        }        var loadConfirmationAfterGap = monitoringStart.AddSeconds(140);
        Check(duplicateConfirmation.Observe(loadIssuesAt(loadConfirmationAfterGap), loadConfirmationAfterGap).Count == 2,
            "Eight fresh resource readings did not trigger following a cache gap");

        var hwiConfirmAt = monitoringStart.AddSeconds(1);
        var hwiCpuAndMemoryBelowThreshold = new HwInfoSnapshot("fixture", "8.54", hwiConfirmAt, null, null, null, null,
            [new HwInfoSensorReading("Total CPU Usage", 55, "%", "CpuLoad"),
             new HwInfoSensorReading("Physical Memory Load", 60, "%", "MemoryLoad")]);
        var osLoadWithHwi = CriticalMonitoringEvaluator.Evaluate(overloaded with
        {
            StartedAt = hwiConfirmAt, HwInfo = hwiCpuAndMemoryBelowThreshold,
            CpuPercentCapturedAtUtc = hwiConfirmAt, MemoryUsedPercentCapturedAtUtc = hwiConfirmAt
        });
        Check(osLoadWithHwi.Where(issue => issue.Identity is "cpu:load" or "memory:load")
            .All(issue => issue.Detail.Contains("Показание HWiNFO не превышает порог ИТ-Сети", StringComparison.Ordinal)),
            "CPU/RAM incidents omitted a negative HWiNFO confirmation");
        var confirmingHwi = new HwInfoSnapshot("fixture", "8.54", hwiConfirmAt, null, null, null, null,
            [new HwInfoSensorReading("Total CPU Usage", 99, "%", "CpuLoad"),
             new HwInfoSensorReading("Physical Memory Load", 99, "%", "MemoryLoad")]);
        var confirmedLoadDetails = CriticalMonitoringEvaluator.Evaluate(overloaded with
        {
            StartedAt = hwiConfirmAt, HwInfo = confirmingHwi,
            CpuPercentCapturedAtUtc = hwiConfirmAt, MemoryUsedPercentCapturedAtUtc = hwiConfirmAt
        });
        Check(confirmedLoadDetails.Where(issue => issue.Identity is "cpu:load" or "memory:load")
            .All(issue => issue.Detail.Contains("Показание HWiNFO превышает порог ИТ-Сети", StringComparison.Ordinal)),
            "CPU/RAM incidents omitted a positive HWiNFO confirmation");

        var osTemperatureWithHwi = CriticalMonitoringEvaluator.Evaluate(new DiagnosticSnapshot(Guid.NewGuid(), hwiConfirmAt,
            "fixture", 0, 0, 0, [], [], CpuTemperatureC: 95, HwInfo: new HwInfoSnapshot("fixture", "8.54",
                hwiConfirmAt, null, null, null, null, [new HwInfoSensorReading("CPU Package", 70, "°C", "CpuTemperature")]),
            CpuTemperatureCapturedAtUtc: hwiConfirmAt));
        Check(osTemperatureWithHwi.Single(issue => issue.Identity == "cpu:temperature").Detail
            .Contains("Показание HWiNFO не превышает порог ИТ-Сети", StringComparison.Ordinal),
            "Temperature incidents omitted a negative HWiNFO confirmation");

        var staleHwInfo = new HwInfoSnapshot("fixture", "8.54", monitoringStart.AddSeconds(-16), null, null, null, null,
            [new HwInfoSensorReading("fixture rail", 14, "V", "Rail12V", IsCritical: true)]);
        Check(CriticalMonitoringEvaluator.Evaluate(overloaded with { HwInfo = staleHwInfo })
                .All(issue => !issue.Identity.StartsWith("hwinfo:", StringComparison.Ordinal)),
            "Stale HWiNFO cache was treated as a fresh critical measurement");

        var hwInfoFixture = Path.Combine(Path.GetTempPath(), "ITSeti-HWiNFO-diagnostics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(hwInfoFixture);
        var hwInfoExecutable = Path.Combine(hwInfoFixture, "HWiNFO32.exe");
        File.WriteAllBytes(hwInfoExecutable, [0]);
        File.WriteAllText(Path.Combine(hwInfoFixture, "HWiNFO32.INI"),
            "[Settings]\r\nSensorsOnly=1\r\nSensorsSM=1\r\nSensorsSM2=1\r\nSensorInterval=5000\r\n");
        try
        {
            var describe = typeof(HwInfoReader).GetMethod("DescribeSession",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
            var details = (string)describe.Invoke(null, [hwInfoExecutable])!;
            Check(details.Contains("SensorsSM2=1", StringComparison.Ordinal) &&
                details.Contains("SensorInterval=5000", StringComparison.Ordinal) &&
                details.Contains("процесс=не запущен", StringComparison.Ordinal),
                "HWiNFO failure diagnostics omitted the adjacent settings or process state");
        }
        finally { Directory.Delete(hwInfoFixture, recursive: true); }

        var serviceCoordinator = System.Reflection.Assembly.Load("ITSeti.Maintenance.Service")
            .GetType("ITSeti.Maintenance.Service.MaintenanceServiceCoordinator")!;
        var exitDescription = serviceCoordinator.GetMethod("DescribeProcessExit",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var crash = (string)exitDescription.Invoke(null, [-1073741819])!;
        Check(crash.Contains("STATUS_ACCESS_VIOLATION", StringComparison.Ordinal) &&
            crash.Contains("0xC0000005", StringComparison.Ordinal),
            "PowerShell access-violation exit code was not decoded for server reports");
        CheckBridge(Check);

        var values = new[] {
            Sensor(" / Total CPU Usage", 32, "%"), Sensor(" / Max CPU/Thread Usage", 100, "%"),
            Sensor(" / Total CPU Utility", 120, "%"), Sensor("CPU Package", 61, "°C"),
            Sensor("CPU Package", 800, "MHz"), Sensor("CPU temperature", 70, "°F"),
            Sensor("GPU PCIe +12V Input Voltage", 13.8, "V"), Sensor("+12V", 12.1, "V"),
            Sensor("CPU Core VID", 1.5, "V"), Sensor("PCH", double.NaN, "°C"),
            Sensor("Physical Memory Load", double.PositiveInfinity, "%"), Sensor("+12V", 12.1, "V")
        };
        var selected = HwInfoReader.MapReadings(values);
        Check(selected.Length == 3, "Unexpected sensor admitted or exact duplicates kept");
        var hw = new HwInfoSnapshot("fixture", "8.54", DateTimeOffset.UtcNow, null, null, null, null, selected);
        Check(hw.CpuLoadPercent == 32 && hw.CpuTemperatureC == 61 && hw.Rail12V == 12.1, "Wrong aggregate sensor");
        HwInfoVoltageStabilizer.Reset();
        var rail = selected.Single(x => x.Category == "Rail12V");
        HwInfoVoltageStabilizer.Stabilize([rail]);
        HwInfoVoltageStabilizer.Stabilize([rail]);
        var stable = HwInfoVoltageStabilizer.Stabilize([rail with { Value = 12.5 }]).Single();
        Check(stable.Value == 12.1 && stable.RawValue == 12.5, "Raw voltage was discarded");
        HwInfoVoltageStabilizer.Reset();

        var snapshot = new DiagnosticSnapshot(Guid.NewGuid(), DateTimeOffset.UtcNow, "fixture", 32, 16UL << 30, 8UL << 30, [], [], HwInfo: hw);
        var json = JsonSerializer.Serialize(snapshot);
        using var raw = JsonDocument.Parse(json);
        Check(!raw.RootElement.TryGetProperty("ResultLabel", out _) && !raw.RootElement.GetProperty("HwInfo").TryGetProperty("CpuTemperatureC", out _), "Presentation leaked into raw report");
        var restored = JsonSerializer.Deserialize<DiagnosticSnapshot>(json)!;
        Check(restored.HwInfo?.CpuTemperatureC == 61, "Raw report roundtrip lost sensor");
        var result = DiagnosticEngine.Evaluate(restored);
        Check(result.RulesVersion == "2.4.1" && result.DataQuality.Single(x => x.Area == "HWiNFO").Status == "Unverified", "Source freshness falsely verified");
        Check(DiagnosticEngine.Evaluate(snapshot with { HwInfo = null }).DataQuality[0].Status == "Unavailable", "Missing sensor treated as normal");
        Check(DiagnosticEngine.Evaluate(snapshot with { HwInfo = hw with { CapturedAtUtc = snapshot.StartedAt.AddMinutes(-10) } }).DataQuality[0].Status == "Stale", "Stale sensor not marked");
        Console.WriteLine("PASS shared rules/report roundtrip, missing/stale quality, HWiNFO selection/units/NaN/GPU/raw voltage, queue concurrency/cancellation/retry");
        return 0;
    }

    private static void CheckBridge(Action<bool, string> check)
    {
        var name = "ITSeti.HwInfo.Fixture." + Guid.NewGuid().ToString("N");
        using var memory = System.IO.MemoryMappedFiles.MemoryMappedFile.CreateNew(name, 1024);
        using var view = memory.CreateViewAccessor();
        const long sensorOffset = 44;
        const long readingOffset = 308;
        view.Write(0, 0x53695748u); view.Write(4, 2u);
        view.Write(12, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        view.Write(20, (uint)sensorOffset); view.Write(24, 264u); view.Write(28, 1u);
        view.Write(32, (uint)readingOffset); view.Write(36, 316u); view.Write(40, 1u);
        void Text(long offset, string value) { var bytes = System.Text.Encoding.ASCII.GetBytes(value); view.WriteArray(offset, bytes, 0, bytes.Length); }
        Text(sensorOffset + 8, "Motherboard");
        view.Write(readingOffset + 4, 0u); view.Write(readingOffset + 8, 42u);
        Text(readingOffset + 140, "+12V"); Text(readingOffset + 268, "V"); view.Write(readingOffset + 284, 12.1);
        var actual = ITSeti.HwInfoBridge.SensorReader.Read(name).Single();
        check(actual.Name == "Motherboard / +12V" && actual.Value == 12.1 && actual.ReadingId == 42 && actual.SourceUnixTime > 0, "Bridge source metadata/offsets incorrect");
        void Rejected(string reason)
        {
            try { ITSeti.HwInfoBridge.SensorReader.Read(name); }
            catch (InvalidDataException) { return; }
            throw new Exception(reason);
        }
        view.Write(12, 0L); Rejected("Zero source timestamp accepted");
        view.Write(12, DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds()); Rejected("Stale source accepted");
        view.Write(12, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        view.Write(40, uint.MaxValue); Rejected("Out-of-bounds shared memory accepted");
        view.Write(40, 1u); view.Write(readingOffset + 284, double.NaN);
        check(ITSeti.HwInfoBridge.SensorReader.Read(name).Length == 0, "Bridge accepted NaN");
        view.Write(0, 0u); Rejected("Corrupt header accepted");
    }

    private static ITSeti.HwInfoBridge.SensorValue Sensor(string name, double value, string unit) => new() { Name = name, Value = value, Unit = unit };
}
