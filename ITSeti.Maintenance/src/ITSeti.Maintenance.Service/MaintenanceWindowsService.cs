using System.ServiceProcess;
using ITSeti.Maintenance.Infrastructure;

namespace ITSeti.Maintenance.Service;

internal sealed class MaintenanceWindowsService : ServiceBase
{
    private CancellationTokenSource? cancellation;
    private MaintenanceServiceCoordinator? coordinator;
    private Task? running;

    public MaintenanceWindowsService()
    {
        ServiceName = MaintenanceServiceProtocol.ServiceName;
        CanStop = true;
        CanShutdown = true;
        AutoLog = true;
    }

    protected override void OnStart(string[] args)
    {
        cancellation = new CancellationTokenSource();
        coordinator = new MaintenanceServiceCoordinator();
        coordinator.ValidateInstallation();
        running = Task.Run(() => coordinator.RunAsync(cancellation.Token));
        _ = running.ContinueWith(task =>
            Environment.FailFast("Служба обслуживания аварийно остановилась.", task.Exception),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    protected override void OnStop() => StopCoordinator();

    protected override void OnShutdown() => StopCoordinator();

    private void StopCoordinator()
    {
        cancellation?.Cancel();
        try { running?.Wait(TimeSpan.FromSeconds(25)); } catch (AggregateException) { }
        coordinator?.Dispose();
        cancellation?.Dispose();
        coordinator = null;
        cancellation = null;
        running = null;
    }
}
