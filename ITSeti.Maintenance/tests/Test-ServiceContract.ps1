param([string]$Root=(Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference='Stop'
$serviceProject=Get-Content -LiteralPath (Join-Path $Root 'src\ITSeti.Maintenance.Service\ITSeti.Maintenance.Service.csproj') -Raw
$coordinator=Get-Content -LiteralPath (Join-Path $Root 'src\ITSeti.Maintenance.Service\MaintenanceServiceCoordinator.cs') -Raw
$protocol=Get-Content -LiteralPath (Join-Path $Root 'src\ITSeti.Maintenance.Infrastructure\MaintenanceServiceProtocol.cs') -Raw
$client=Get-Content -LiteralPath (Join-Path $Root 'src\ITSeti.Maintenance.Infrastructure\MaintenanceServiceClient.cs') -Raw
$install=Get-Content -LiteralPath (Join-Path $Root 'Install-Maintenance.ps1') -Raw
$uninstall=Get-Content -LiteralPath (Join-Path $Root 'Uninstall-Maintenance.ps1') -Raw
$builder=Get-Content -LiteralPath (Join-Path $Root 'Build-InstallPackage.ps1') -Raw
$installer=Get-Content -LiteralPath (Join-Path $Root 'Installer.iss') -Raw
if($serviceProject -notmatch '<Version>2\.3\.2</Version>' -or
   !$protocol.Contains('ITSetiMaintenanceService') -or !$protocol.Contains('ITSeti.Maintenance.Service.v1') -or
   !$coordinator.Contains('BuiltinUsersSid') -or !$coordinator.Contains('PipeAccessRights.ReadWrite') -or
   !$coordinator.Contains('ResolveOperation') -or $coordinator.Contains('ProcessStartInfo(request') -or
   !$client.Contains('NamedPipeClientStream') -or !$client.Contains('CancelAfter(TimeSpan.FromSeconds(3))')) {
    throw 'The service IPC contract is missing its version, ACL, timeout, or fixed operation allowlist.'
}
foreach($operation in @('FullCheck','QuickCheck','DailySmartCheck','ScheduledFullCheck','Cleanup','Repair','OrganizationSetup','UploadReports','RefreshSoftwareCatalog','ProbeTemperature','DisableWindowsUpdates','RestoreWindowsUpdates')) {
    if(!$coordinator.Contains('MaintenanceServiceOperation.'+$operation)){throw "Service operation is missing: $operation"}
}
if(!$protocol.Contains('OpenSystemTool') -or !$protocol.Contains('OpenUtility') -or
   !$client.Contains('LaunchSystemToolAsync') -or !$client.Contains('LaunchUtilityAsync') -or
   !$coordinator.Contains('LaunchInteractiveTool') -or !$coordinator.Contains('ElevatedInteractiveProcessLauncher') -or
   !$coordinator.Contains('SessionId') -or !$coordinator.Contains('IsAdministrativeClient') -or
   !$coordinator.Contains('IsAdministratorOnlyLaunch')) { throw 'Privileged engineering-menu IPC is missing.' }
if(!$coordinator.Contains('MonitoringQueue') -or !$coordinator.Contains('CriticalMonitoringEvaluator') -or
   !$coordinator.Contains('DiskReadLatencyMs') -or !$coordinator.Contains('DiskWriteLatencyMs')) {
    throw 'Continuous critical monitoring is missing disk latency or its protected queue.'
}
if(!$coordinator.Contains('MaintenanceServiceOperation.Cleanup => (Folder: "CleanupRequests", Pattern: "*.request")') -or
   !$coordinator.Contains('if (HasPendingRequests(operation))')) {
    throw 'Cleanup requests arriving during an active service run must be picked up by a follow-up run.'
}
if(!$install.Contains("`$serviceName='ITSetiMaintenanceService'") -or !$install.Contains('New-Service -Name $serviceName') -or
   !$install.Contains('start= delayed-auto') -or !$install.Contains('Start-Service -Name $serviceName') -or
   !$uninstall.Contains("`$serviceName='ITSetiMaintenanceService'") -or !$uninstall.Contains('sc.exe delete $serviceName') -or
   !$builder.Contains('dotnet publish $serviceProject') -or !$installer.Contains('{#PackageRoot}\Service\*')) {
    throw 'Build, install, update, or uninstall integration for the service is incomplete.'
}
'PASS: service IPC, allowlisted operations, package, install and uninstall contracts'
