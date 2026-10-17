using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using ITSeti.Maintenance.App;
using ITSeti.Maintenance.Core;
using ITSeti.Maintenance.Infrastructure;

internal static class FeedbackRegressionChecks
{
    public static int Run(bool liveHardware)
    {
        CheckSensors();
        CheckOfflineNotifications().GetAwaiter().GetResult();
        CheckUpdaterPolling().GetAwaiter().GetResult();
        CheckLiveOverviewAndClosing();
        if (liveHardware)
        {
            var actual = ComputerHardwareIdentity.Read();
            if (string.IsNullOrWhiteSpace(actual.Gpu) || string.IsNullOrWhiteSpace(actual.MemoryType))
                throw new Exception("Live hardware identification returned incomplete data on this test PC.");
            Console.WriteLine($"PASS live hardware: {actual.Gpu}; {actual.MemoryType}");
        }
        return 0;
    }

    private static void CheckSensors()
    {
        foreach (var name in new[] { "CPU / Thermal Throttling", "GPU / Performance Limit - Thermal",
                     "VRM / VR Thermal Alert", "NVMe / Drive Critical Warning", "GPU / Performance Limit - Power" })
            if (HwInfoSensorPolicy.Classify(name, "Yes/No") != "HardwareAlarm") throw new Exception("Missing alarm: " + name);
        if (HwInfoSensorPolicy.Classify("GPU / Performance Limit - Utilization", "Yes/No") is not null ||
            HwInfoSensorPolicy.Classify("AC Connected", "Yes/No") is not null)
            throw new Exception("A normal boolean state became a hardware alarm.");
        if (HwInfoSensorPolicy.Classify("Motherboard / VR VCC Temperature", "°C") != "VrmTemperature")
            throw new Exception("VR VCC temperature was lost.");
        var sensors = new[] {
            new HwInfoSensorReading("CPU [#0] / Core 0", 60, "°C", "CpuTemperature"),
            new HwInfoSensorReading("CPU [#0] / Core 1", 80, "°C", "CpuTemperature"),
            new HwInfoSensorReading("CPU [#0] / CPU Die (average)", 68, "°C", "CpuTemperature"),
            new HwInfoSensorReading("GPU / Thermal Throttling", 1, "Yes/No", "HardwareAlarm", IsWarning: true),
            new HwInfoSensorReading("Board / VRM MOS", 57, "°C", "VrmTemperature")
        };
        var display = HwInfoSensorPolicy.DisplaySensors(sensors);
        if (display.Any(item => item.Category == "HardwareAlarm") ||
            display.Single(item => item.Category == "CpuTemperature").Value != 68 ||
            !display.Any(item => item.Category == "VrmTemperature")) throw new Exception("Sensor screen contains per-core alarms or lost VRM/CPU average.");
        var fallback = HwInfoSensorPolicy.DisplaySensors(sensors.Take(2)).Single();
        if (fallback.Value != 70 || fallback.Quality != "DerivedDisplayAverage") throw new Exception("CPU core average fallback is wrong.");
        var now = DateTimeOffset.UtcNow;
        var sample = new DiagnosticSnapshot(Guid.NewGuid(), now, "Fixture", 0, 100, 50, [], [],
            HwInfo: new HwInfoSnapshot("fixture", "", now, null, null, null, null, sensors, "SourceTimestampChecked"));
        var alarm = CriticalMonitoringEvaluator.Evaluate(sample).Single(item => item.Identity.StartsWith("hwinfo:critical:"));
        if (alarm.Severity != "Warning" || !alarm.Detail.Contains("аппаратный флаг активен"))
            throw new Exception("Hardware alarm lost its meaning/severity.");
        Console.WriteLine("PASS: CPU/GPU/VRM/drive hardware flags, normal-state exclusions, CPU aggregate and VRM display");
    }

    private static async Task CheckOfflineNotifications()
    {
        var root = Path.Combine(Path.GetTempPath(), "itseti-offline-reply-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Service"));
        var device = Guid.NewGuid().ToString();
        var messageId = Guid.NewGuid();
        var statePath = Path.Combine(root, "Service", "ticket-updates.json");
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        try
        {
            File.WriteAllText(Path.Combine(root, "server-support.json"), JsonSerializer.Serialize(new {
                serverUrl = "https://it-seti.nylenz.ru/", deviceId = device, supportKey = "fixture-only" }));
            File.WriteAllText(statePath, JsonSerializer.Serialize(new TicketUpdateState(device, DateTimeOffset.UtcNow.AddHours(-8),
                Guid.Empty, 1, DateTimeOffset.UtcNow.AddHours(-8), []), options));
            var handler = new ReplyHandler(messageId);
            using (var beforeLogon = new TicketUpdateMonitor(root, handler, tryDeliverMessages: _ => false))
            {
                if (await beforeLogon.PollAsync() != TimeSpan.FromSeconds(30)) throw new Exception("Undelivered reply polling was suspended before logon.");
                if (JsonSerializer.Deserialize<TicketUpdateState>(File.ReadAllText(statePath), options)?.PendingNotifications?.Count != 1)
                    throw new Exception("Reply received before logon was not persisted.");
            }
            handler.IncludeReply = false;
            var delivered = 0;
            using (var afterRestart = new TicketUpdateMonitor(root, handler, tryDeliverMessages: messages => {
                delivered += messages.Count; return true;
            }))
            {
                if (await afterRestart.PollAsync() != Timeout.InfiniteTimeSpan) throw new Exception("Completed ticket kept polling after notification delivery.");
                await afterRestart.PollAsync();
            }
            if (delivered != 1 || JsonSerializer.Deserialize<TicketUpdateState>(File.ReadAllText(statePath), options)?.PendingNotifications?.Count != 0)
                throw new Exception("Offline reply was lost or delivered repeatedly after restart.");
            Console.WriteLine("PASS: reply while PC offline, boot before user logon, persisted notification, one delivery after logon, idle after completion");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void CheckLiveOverviewAndClosing()
    {
        var app = new App(); app.InitializeComponent(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        CheckUpdaterWindow();
        var root = Path.Combine(Path.GetTempPath(), "itseti-feedback-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var window = new MainWindow(Path.Combine(root, "history.db"));
        var model = window.ViewModel;
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var full = new FullDiagnosticDetails("CPU fixture", "GPU fixture", false, [], [], [], [], 0, 0, false,
            new DiskBenchmark("C:", "SSD", null, null, 0, "Skipped", ""), "", MemoryType: "DDR4");
        var saved = new DiagnosticSnapshot(Guid.NewGuid(), DateTimeOffset.UtcNow, "Fixture", 5, 100, 90,
            [new("C:", 1000, 900)], [], Full: full);
        model.Selected = saved;
        var sensorTime = DateTimeOffset.UtcNow;
        model.Selected = saved with { HwInfo = new HwInfoSnapshot("fixture", "", sensorTime, null, null, null, null,
            [new("Board / VRM MOS", 57, "°C", "VrmTemperature"),
             new("CPU / Total CPU Usage", 30, "%", "CpuLoad"),
             new("Board / +12V", 12.1, "V", "Rail12V")]) };
        typeof(MainViewModel).GetMethod("RefreshHwInfoSensors", flags)!.Invoke(model, null);
        if (model.HwInfoSensors.Count != 3 || model.HwInfoSensors.Any(sensor => sensor.Name.Contains('/')) ||
            model.HwInfoSensors.Select(sensor => sensor.Group).Distinct().Count() != 3)
            throw new Exception("Sensor labels retained source prefixes or lost type grouping.");
        model.Selected = saved;
        typeof(MainViewModel).GetField("live", flags)!.SetValue(model, saved with { Full = null, Disks = [new("C:", 1000, 100)] });
        if (model.Gpu != "GPU fixture" || model.MemoryType != "DDR4" || !model.DiskCount.Contains("90"))
            throw new Exception("Live resource sampling erased hardware identity or overview disk usage.");
        using var cancellation = new CancellationTokenSource();
        typeof(MainViewModel).GetField("fullCheckCancellation", flags)!.SetValue(model, cancellation);
        window.Close();
        var waitsForPortableCheck = (bool)typeof(MainWindow).GetField("closeAfterCheck", flags)!.GetValue(window)!;
        if (cancellation.IsCancellationRequested || waitsForPortableCheck == FullDiagnosticsRunner.IsInstalled)
            throw new Exception("Closing the window interrupted the check.");
        typeof(MainViewModel).GetField("fullCheckCancellation", flags)!.SetValue(model, null);
        window.Close(); app.Shutdown();
        Directory.Delete(root, true);
        Console.WriteLine("PASS: live monitoring preserves GPU/DDR, overview uses live disk usage, installed UI closes without cancelling service work");
    }

    private static async Task CheckUpdaterPolling()
    {
        var path = Path.GetTempFileName();
        try
        {
            var runner = new ApplicationUpdateRunner(path, async (operation, token) => {
                if (operation == "/Run")
                    await File.WriteAllTextAsync(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") +
                        " | Пакет загружен и проверен. Ожидает закрытия приложения.", token);
            });
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            if (!await runner.RequestUpdateAsync(cancellationToken: timeout.Token))
                throw new Exception("Updater missed verified pending-install status.");
            await File.WriteAllTextAsync(path, "2020-01-01 00:00:00 | Пакет загружен и проверен. Ожидает закрытия приложения.");
            runner = new ApplicationUpdateRunner(path, (_, _) => Task.CompletedTask);
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            try { await runner.RequestUpdateAsync(cancellationToken: cancel.Token); throw new Exception("Stale pending status was accepted."); }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
            Console.WriteLine("PASS: updater accepts overwritten ready status, rejects stale status, supports cancellation");
        }
        finally { File.Delete(path); }
    }

    private static void CheckUpdaterWindow()
    {
        var cancelled = false;
        var window = new UpdateProgressWindow("fixture", async (_, token) => {
            try { await Task.Delay(Timeout.Infinite, token); return false; }
            catch (OperationCanceledException) { cancelled = true; throw; }
        });
        window.Show();
        window.Close();
        var frame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
        if (window.IsVisible || !cancelled) throw new Exception("Update dialog blocked close or retained its polling task.");
        Console.WriteLine("PASS: closing update dialog cancels polling and leaves window closable");
    }

    private sealed class ReplyHandler(Guid id) : HttpMessageHandler
    {
        public bool IncludeReply { get; set; } = true;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new {
                activeCount = 0, hasMore = false, cursor = DateTimeOffset.UtcNow, cursorId = Guid.Empty,
                messages = IncludeReply ? new[] { new { id, title = "Fixture answer", createdAt = DateTimeOffset.UtcNow } } : []
            }), Encoding.UTF8, "application/json") });
    }
}
