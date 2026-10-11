using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using ITSeti.Maintenance.Infrastructure;

namespace ITSeti.Maintenance.Service;

internal static class ServiceSelfTest
{
    public static async Task<int> RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "ITSeti-Service-Test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (var name in new[] { "InstalledCheck.ps1", "InstalledCleanup.ps1", "InstalledRepair.ps1",
                         "InstalledOrganizationSetup.ps1", "Upload-Reports.ps1", "ServerSoftware.ps1", "Set-WindowsAutomaticUpdates.ps1" })
                await File.WriteAllTextAsync(Path.Combine(root, name), "exit 0");
            var status = Path.Combine(root, "status.json");
            var pipeName = MaintenanceServiceProtocol.PipeName + ".SelfTest." + Guid.NewGuid().ToString("N");
            using var coordinator = new MaintenanceServiceCoordinator(root, status, pipeName);
            coordinator.ValidateInstallation();
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var running = coordinator.RunAsync(cancellation.Token);
            await SendOversizedRequestAsync(pipeName, cancellation.Token);
            MaintenanceServiceCallResult ping = MaintenanceServiceCallResult.Unavailable("not attempted");
            for (var attempt = 0; attempt < 20 && !ping.Connected; attempt++)
            {
                await Task.Delay(100);
                ping = await PingAsync(pipeName, cancellation.Token);
            }
            if (!ping.Accepted) throw new InvalidOperationException("Проверка канала службы не прошла: " + ping.Message);
            cancellation.Cancel();
            await running;
            Console.WriteLine("PASS: service validation and protected named-pipe ping");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    private static async Task SendOversizedRequestAsync(string pipeName, CancellationToken cancellationToken)
    {
        await using var pipe = new NamedPipeClientStream(".", pipeName,
            PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(cancellationToken);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        await writer.WriteLineAsync(new string('x', MaintenanceServiceProtocol.MaximumMessageCharacters + 1));
        await Task.Delay(100, cancellationToken);
    }

    private static async Task<MaintenanceServiceCallResult> PingAsync(string pipeName, CancellationToken cancellationToken)
    {
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(cancellationToken);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, Encoding.UTF8, true, leaveOpen: true);
        await writer.WriteLineAsync(JsonSerializer.Serialize(new MaintenanceServiceRequest("ping"),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var line = await reader.ReadLineAsync(cancellationToken);
        var response = string.IsNullOrWhiteSpace(line) ? null : JsonSerializer.Deserialize<MaintenanceServiceResponse>(line,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return response is null
            ? MaintenanceServiceCallResult.Unavailable("Служба не вернула ответ.")
            : new MaintenanceServiceCallResult(true, response.Success, response.Message);
    }
}
