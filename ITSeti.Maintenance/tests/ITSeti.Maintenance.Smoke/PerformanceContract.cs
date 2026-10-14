using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using ITSeti.HwInfoBridge;
using ITSeti.Maintenance.Infrastructure;

internal static class PerformanceContract
{
    public static int Run()
    {
        var values = Enumerable.Range(0, 2000).Select(i => new SensorValue
        {
            Name = "Unrelated counter " + i, Unit = "MHz", Value = i, ReadingId = (uint)i
        }).Concat(new[]
        {
            new SensorValue { Name = "CPU Package", Unit = "°C", Value = 55, ReadingId = 2001 },
            new SensorValue { Name = "CPU Package", Unit = "°C", Value = 55, ReadingId = 2001 },
            new SensorValue { Name = "CPU Package", Unit = "°C", Value = double.NaN, ReadingId = 2002 },
            new SensorValue { Name = "Total CPU Usage", Unit = "%", Value = 20, ReadingId = 2003 }
        }).ToArray();
        var result = HwInfoReader.MapReadings(values);
        if (result.Length != 2 || result[0].Value != 55 || result[1].Value != 20)
            throw new Exception("HWiNFO filter must preserve order, remove duplicates and reject invalid/unrelated sensors.");
        for (var i = 0; i < 20; i++) HwInfoReader.MapReadings(values);
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var timer = Stopwatch.StartNew();
        for (var i = 0; i < 200; i++) HwInfoReader.MapReadings(values);
        timer.Stop();
        Console.WriteLine(JsonSerializer.Serialize(new { benchmark = "hwinfo-map", iterations = 200,
            inputSensors = values.Length, elapsedMs = timer.Elapsed.TotalMilliseconds,
            allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated }));

        timer.Restart();
        foreach (var value in new[] { 0d, 20d, 100d })
            if (WindowsDiagnosticsRunner.ReadCpuPercentAsync(value, false, CancellationToken.None).GetAwaiter().GetResult() != value)
                throw new Exception("Valid HWiNFO CPU load was not preserved.");
        timer.Stop();
        Console.WriteLine(JsonSerializer.Serialize(new { benchmark = "fresh-hwinfo-cpu", iterations = 3,
            elapsedMs = timer.Elapsed.TotalMilliseconds }));
        foreach (var value in new double?[] { null, -1, 101, double.NaN, double.PositiveInfinity })
        {
            using var cancel = new CancellationTokenSource();
            cancel.Cancel();
            try
            {
                WindowsDiagnosticsRunner.ReadCpuPercentAsync(value, false, cancel.Token).GetAwaiter().GetResult();
                throw new Exception("Native CPU fallback did not honor cancellation.");
            }
            catch (OperationCanceledException) { }
        }
        CheckStalledTicketResponse().GetAwaiter().GetResult();
        Console.WriteLine("PASS HWiNFO sensor selection, CPU load boundaries, fallback cancellation and bounded ticket response");
        return 0;
    }

    private static async Task CheckStalledTicketResponse()
    {
        var root = Path.Combine(Path.GetTempPath(), "itseti-ticket-timeout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "server-support.json"), JsonSerializer.Serialize(new
            {
                serverUrl = "https://it-seti.nylenz.ru/", deviceId = Guid.NewGuid(), supportKey = "test-key"
            }));
            using var handler = new StalledBodyHandler();
            using var monitor = new TicketUpdateMonitor(root, handler, TimeSpan.FromMilliseconds(100));
            Directory.CreateDirectory(Path.GetDirectoryName(monitor.StatePath)!);
            const string original = "{}";
            await File.WriteAllTextAsync(monitor.StatePath, original);
            var timer = Stopwatch.StartNew();
            try
            {
                await monitor.PollAsync().WaitAsync(TimeSpan.FromSeconds(3));
                throw new Exception("A stalled response body completed without cancellation.");
            }
            catch (OperationCanceledException) { }
            if (!handler.BodyDisposed || timer.Elapsed >= TimeSpan.FromSeconds(3) ||
                await File.ReadAllTextAsync(monitor.StatePath) != original)
                throw new Exception("A stalled ticket response leaked its stream or changed the saved cursor.");
            Console.WriteLine($"PASS stalled ticket body cancelled and disposed in {timer.Elapsed.TotalMilliseconds:F0} ms; cursor preserved");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class StalledBodyHandler : HttpMessageHandler
    {
        public bool BodyDisposed { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new StalledStream(() => BodyDisposed = true))
            });
    }

    private sealed class StalledStream(Action onDispose) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        protected override void Dispose(bool disposing) { if (disposing) onDispose(); base.Dispose(disposing); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
    }
}
