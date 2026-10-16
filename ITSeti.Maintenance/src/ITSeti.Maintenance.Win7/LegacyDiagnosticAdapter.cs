using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ITSeti.Maintenance.Core;

namespace ITSeti.Maintenance.Win7
{
    // Only maps the legacy collection format. All diagnostic decisions belong to Core.
    internal static class LegacyDiagnosticAdapter
    {
        internal static DiagnosticSnapshot ToSnapshot(LegacySnapshot source)
        {
            Guid id; DateTimeOffset started;
            Guid.TryParse(source.Id, out id);
            DateTimeOffset.TryParse(source.StartedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out started);
            var sensors = new List<HwInfoSensorReading>();
            foreach (var sensor in source.HwInfoSensors ?? new List<LegacyHwInfoSensor>())
                if (HwInfoSensorPolicy.IsValidValue(sensor.Category, sensor.Value))
                    sensors.Add(new HwInfoSensorReading(sensor.Name, sensor.Value, sensor.Unit, sensor.Category));
            Add(sensors, "CPU", "CpuTemperature", source.CpuTemperatureC, "°C");
            Add(sensors, "VRM", "VrmTemperature", source.VrmTemperatureC, "°C");
            Add(sensors, "PCH", "PchTemperature", source.PchTemperatureC, "°C");
            Add(sensors, "+12V", "Rail12V", source.Rail12V, "V");
            Add(sensors, "+5V", "Rail5V", source.Rail5V, "V");
            Add(sensors, "+3.3V", "Rail3V", source.Rail3V, "V");
            foreach (var alert in source.HwInfoAlerts ?? new List<LegacyHwInfoAlert>())
                if (HwInfoSensorPolicy.IsValidValue("HardwareAlarm", alert.Value)) sensors.Add(new HwInfoSensorReading(alert.Name ?? "Hardware alarm", alert.Value, alert.Unit ?? "Yes/No", "HardwareAlarm", alert.Status, alert.IsWarning, alert.IsCritical));
            DateTimeOffset sensorTime;
            var sourceTimeKnown = DateTimeOffset.TryParse(source.HwInfoCapturedAtUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out sensorTime);
            var smart = (source.SmartDisks ?? new List<LegacySmartDisk>()).Select(d =>
                new SmartDiskDetails(d.Model ?? "", d.Health ?? "", d.Letters ?? "", d.MediaType ?? "", d.TransferMode ?? "", d.PowerOnHours)).ToList();
            var benchmark = source.Benchmark;
            var system = source.Disks.FirstOrDefault(d => d.IsSystem);
            var full = new FullDiagnosticDetails(source.CpuName ?? "", source.GpuName ?? "", false,
                new List<PhysicalDiskDetails>(), smart, new List<ProcessDetails>(),
                source.Events.Select(e => new EventDetails(e.Log ?? "", e.Source ?? "", (int)e.Id, e.Level, e.Time ?? "", e.Message ?? "")).ToList(),
                0, 0, false, new DiskBenchmark(benchmark?.TargetVolume ?? system?.Name ?? "C:", system?.MediaType ?? "",
                    benchmark?.ReadMbps, benchmark?.WriteMbps, 0, benchmark?.State ?? "Skipped", benchmark?.Error ?? "", "DiskSpd"), "", MemoryType: source.MemoryType);
            return new DiagnosticSnapshot(id, started, source.ComputerName ?? "", source.CpuPercent ?? -1,
                Bytes(source.TotalMemoryGb), Bytes(source.FreeMemoryGb),
                source.Disks.Select(d => new DiskSnapshot(d.Name ?? "", d.TotalBytes, d.FreeBytes)).ToList(),
                new List<string>(), full, WindowsEdition: source.OsName, CpuTemperatureC: source.CpuTemperatureC,
                ActiveUptimeHours: source.ActiveUptimeHours,
                HwInfo: sensors.Count == 0 ? null : new HwInfoSnapshot("HWiNFO (Win7 adapter)", source.HwInfoVersion ?? "", sourceTimeKnown ? sensorTime : started, source.CpuName, null, null, source.MemoryType, sensors, sourceTimeKnown ? "SourceTimestampChecked" : "SourceTimestampUnavailable"),
                SystemDrive: (system?.Name ?? "C:").TrimEnd('\\'),
                CpuTemperatureCapturedAtUtc: sourceTimeKnown ? sensorTime : started,
                CpuPercentCapturedAtUtc: started,
                MemoryUsedPercentCapturedAtUtc: started);
        }

        internal static Dictionary<string, object> ReportFields(LegacySnapshot source)
        {
            var serializer = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
            var report = new Dictionary<string, object>();
                    var input = LegacyDiagnosticAdapter.ToSnapshot(source);
                    var evaluation = ITSeti.Maintenance.Core.DiagnosticEngine.Evaluate(input);
                    var normalized = serializer.Deserialize<Dictionary<string, object>>(serializer.Serialize(input));
                    normalized["StartedAt"] = input.StartedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
                    if (input.HwInfo != null)
                    {
                        var sensors = serializer.Deserialize<Dictionary<string, object>>(serializer.Serialize(input.HwInfo));
                        sensors["CapturedAtUtc"] = input.HwInfo.CapturedAtUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
                        normalized["HwInfo"] = sensors;
                    }
                    report["DiagnosticSnapshot"] = normalized;
                    report["DiagnosticResult"] = evaluation;
                    report["DiagnosticIssues"] = evaluation.Issues;
            // JavaScriptSerializer otherwise writes DateTimeOffset as an object, not an ISO timestamp.
            report["DiagnosticResult"] = new {
                evaluation.SchemaVersion, evaluation.RulesVersion, evaluation.Issues, evaluation.Findings,
                DataQuality = evaluation.DataQuality.Select(q => new { q.Area, q.Status, q.Source,
                    ReadAtUtc = q.ReadAtUtc?.ToString("O", CultureInfo.InvariantCulture), q.Freshness }).ToArray()
            };
            return report;
        }

        internal static DiagnosticEvaluation Evaluate(LegacySnapshot source) => DiagnosticEngine.Evaluate(ToSnapshot(source));
        internal static void Apply(LegacySnapshot source)
        {
            var result = Evaluate(source);
            source.Findings = result.Issues.Select(issue => issue.Title + ". " + issue.Detail).ToList();
        }
        private static ulong Bytes(double? gib) => gib.HasValue && Numeric.IsFinite(gib.Value) && gib.Value > 0 ? (ulong)(gib.Value * 1073741824.0) : 0;
        private static void Add(List<HwInfoSensorReading> values, string name, string category, double? value, string unit)
        {
            if (value.HasValue && HwInfoSensorPolicy.IsValidValue(category, value.Value) && !values.Any(item => item.Category == category))
                values.Add(new HwInfoSensorReading(name, value.Value, unit, category));
        }
    }
}
