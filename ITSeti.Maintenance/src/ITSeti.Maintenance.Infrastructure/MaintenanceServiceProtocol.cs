namespace ITSeti.Maintenance.Infrastructure;

public enum MaintenanceServiceOperation
{
    FullCheck,
    QuickCheck,
    ScheduledFullCheck,
    Cleanup,
    Repair,
    OrganizationSetup,
    UploadReports,
    RefreshSoftwareCatalog,
    ProbeTemperature,
    DisableWindowsUpdates,
    RestoreWindowsUpdates
}

public sealed record MaintenanceServiceRequest(string Command, MaintenanceServiceOperation? Operation = null);

public sealed record MaintenanceServiceResponse(bool Success, string Message, string? Version = null);

public static class MaintenanceServiceProtocol
{
    public const string ServiceName = "ITSetiMaintenanceService";
    public const string PipeName = "ITSeti.Maintenance.Service.v1";
    public const int MaximumMessageCharacters = 4096;
}
