namespace ITSeti.Maintenance.Infrastructure;

public enum MaintenanceServiceOperation
{
    FullCheck,
    QuickCheck,
    DailySmartCheck,
    ScheduledFullCheck,
    Cleanup,
    Repair,
    OrganizationSetup,
    UploadReports,
    RefreshSoftwareCatalog,
    ProbeTemperature,
    DisableWindowsUpdates,
    RestoreWindowsUpdates,
    DisableWindowsDefender,
    EnableWindowsDefender,
    MinimizeWindowsDiagnosticData,
    RestoreWindowsDiagnosticData,
    OpenSystemTool,
    OpenUtility,
    OpenServerPackage,
    ReadWindowsDefenderStatus,
    UpdateServerSoftware,
    RemoveLocalAdministrator
}

public sealed record MaintenanceServiceRequest(
    string Command,
    MaintenanceServiceOperation? Operation = null,
    string? Target = null,
    string? Argument = null,
    int? SessionId = null,
    string? Password = null,
    string? AuthorizationToken = null,
    DeviceEnrollmentData? Enrollment = null);

public sealed record DeviceEnrollmentData(string ServerUrl, string DeviceId, string DeviceKey,
    string? SupportKey, long? CompanyId, long? SiteId);

public sealed record MaintenanceServiceResponse(bool Success, string Message, string? Version = null,
    string? AuthorizationToken = null);

public static class MaintenanceServiceProtocol
{
    public const string ServiceName = "ITSetiMaintenanceService";
    public const string PipeName = "ITSeti.Maintenance.Service.v1";
    public const int MaximumMessageCharacters = 4096;
}
