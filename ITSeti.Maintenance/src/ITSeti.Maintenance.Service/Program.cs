using System.ServiceProcess;

namespace ITSeti.Maintenance.Service;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
            return await ServiceSelfTest.RunAsync();

        if (args.Contains("--console", StringComparer.OrdinalIgnoreCase))
        {
            using var coordinator = new MaintenanceServiceCoordinator();
            using var cancellation = new CancellationTokenSource();
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellation.Cancel();
            };
            await coordinator.RunAsync(cancellation.Token);
            return 0;
        }

        ServiceBase.Run(new MaintenanceWindowsService());
        return 0;
    }
}
