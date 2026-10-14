using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using ITSeti.Maintenance.Infrastructure;

namespace ITSeti.Maintenance.Service;

internal static class ServiceSelfTest
{
    public static async Task<int> RunAsync()
    {
        if (Environment.GetEnvironmentVariable("ITSETI_TEST_ENGINEER_PASSWORD") is { Length: > 0 } password)
        {
            var authorization = new EngineerAuthorization();
            var client = new ServiceClientIdentity(true, Environment.ProcessId, 1);
            if (authorization.Authenticate(password + "!", client) is not null)
                throw new InvalidOperationException("Неверный пароль инженера принят.");
            var token = authorization.Authenticate(password, client);
            if (token is null || !authorization.IsAuthorized(token, client) ||
                authorization.IsAuthorized(token, client with { ProcessId = Environment.ProcessId + 1 }))
                throw new InvalidOperationException("Пароль инженера или привязка токена к процессу неверны.");
        }
        var root = Path.Combine(Path.GetTempPath(), "ITSeti-Service-Test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (var name in new[] { "InstalledCheck.ps1", "InstalledCleanup.ps1", "InstalledRepair.ps1",
                         "InstalledOrganizationSetup.ps1", "Upload-Reports.ps1", "ServerSoftware.ps1", "Set-WindowsAutomaticUpdates.ps1", "WindowsDefender.ps1" })
                await File.WriteAllTextAsync(Path.Combine(root, name), "exit 0");
            var status = Path.Combine(root, "status.json");
            var pipeName = MaintenanceServiceProtocol.PipeName + ".SelfTest." + Guid.NewGuid().ToString("N");
            using var coordinator = new MaintenanceServiceCoordinator(root, status, pipeName, monitorEnabled: false);
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
            var rejected = await SendAsync(pipeName, new MaintenanceServiceRequest("start", MaintenanceServiceOperation.Repair), cancellation.Token);
            if (!rejected.Connected || rejected.Accepted || !rejected.Message.Contains("установленное приложение", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Неустановленный клиент получил доступ к системной операции: " + rejected.Message);
            var enrollmentPath = Path.Combine(root, "server-device.json");
            var enrollment = new DeviceEnrollmentData("https://it-seti.nylenz.ru/", Guid.NewGuid().ToString(),
                new string('a', 32), "support-test", 1, 2);
            MaintenanceServiceCoordinator.SaveEnrollment(enrollment, enrollmentPath);
            MaintenanceServiceCoordinator.SaveEnrollment(enrollment with { SiteId = 3 }, enrollmentPath);
            var saved = JsonSerializer.Deserialize<DeviceEnrollmentData>(await File.ReadAllTextAsync(enrollmentPath),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (saved?.SiteId != 3 || !File.Exists(Path.Combine(root, "server-support.json")))
                throw new InvalidOperationException("Служба не сохранила привязку и ключ поддержки.");
            var deniedDirectory = Path.Combine(root, "denied");
            Directory.CreateDirectory(deniedDirectory);
            using (var identity = WindowsIdentity.GetCurrent())
            {
                var security = new DirectoryInfo(deniedDirectory).GetAccessControl();
                security.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.CreateFiles,
                    AccessControlType.Deny));
                new DirectoryInfo(deniedDirectory).SetAccessControl(security);
            }
            try
            {
                MaintenanceServiceCoordinator.SaveEnrollment(enrollment, Path.Combine(deniedDirectory, "server-device.json"));
                throw new InvalidOperationException("Тест записи привязки проигнорировал запрещающий ACL.");
            }
            catch (UnauthorizedAccessException ex) when (ex.Message.Contains("Windows identity=", StringComparison.Ordinal) &&
                ex.Message.Contains("права каталога:", StringComparison.Ordinal) && ex.Message.Contains("DENY:", StringComparison.Ordinal)) { }
            try
            {
                MaintenanceServiceCoordinator.SaveEnrollment(enrollment with { DeviceKey = new string('b', 32) }, enrollmentPath);
                throw new InvalidOperationException("Служба приняла подмену ключа ПК.");
            }
            catch (InvalidDataException) { }
            cancellation.Cancel();
            await running;
            Console.WriteLine("PASS: service validation, ping and rejection of an unauthorized client");
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
        => await SendAsync(pipeName, new MaintenanceServiceRequest("ping"), cancellationToken);

    private static async Task<MaintenanceServiceCallResult> SendAsync(string pipeName,
        MaintenanceServiceRequest request, CancellationToken cancellationToken)
    {
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(cancellationToken);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, Encoding.UTF8, true, leaveOpen: true);
        await writer.WriteLineAsync(JsonSerializer.Serialize(request,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var line = await reader.ReadLineAsync(cancellationToken);
        var response = string.IsNullOrWhiteSpace(line) ? null : JsonSerializer.Deserialize<MaintenanceServiceResponse>(line,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return response is null
            ? MaintenanceServiceCallResult.Unavailable("Служба не вернула ответ.")
            : new MaintenanceServiceCallResult(true, response.Success, response.Message);
    }
}
