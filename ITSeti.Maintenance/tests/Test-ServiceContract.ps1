param([string]$Root=(Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference='Stop'
$serviceProject=Get-Content -LiteralPath (Join-Path $Root 'src\ITSeti.Maintenance.Service\ITSeti.Maintenance.Service.csproj') -Raw
$coordinator=Get-Content -LiteralPath (Join-Path $Root 'src\ITSeti.Maintenance.Service\MaintenanceServiceCoordinator.cs') -Raw
$protocol=Get-Content -LiteralPath (Join-Path $Root 'src\ITSeti.Maintenance.Infrastructure\MaintenanceServiceProtocol.cs') -Raw
$client=Get-Content -LiteralPath (Join-Path $Root 'src\ITSeti.Maintenance.Infrastructure\MaintenanceServiceClient.cs') -Raw
$diagnostics=Get-Content -LiteralPath (Join-Path $Root 'src\ITSeti.Maintenance.Infrastructure\WindowsDiagnosticsRunner.cs') -Raw
$install=Get-Content -LiteralPath (Join-Path $Root 'Install-Maintenance.ps1') -Raw
$uninstall=Get-Content -LiteralPath (Join-Path $Root 'Uninstall-Maintenance.ps1') -Raw
$builder=Get-Content -LiteralPath (Join-Path $Root 'Build-InstallPackage.ps1') -Raw
$installer=Get-Content -LiteralPath (Join-Path $Root 'Installer.iss') -Raw
$ticketMonitor=Get-Content -LiteralPath (Join-Path $Root 'src\ITSeti.Maintenance.Infrastructure\TicketUpdateMonitor.cs') -Raw
$notifierProject=Get-Content -LiteralPath (Join-Path $Root 'src\ITSeti.Maintenance.TicketNotifier\ITSeti.Maintenance.TicketNotifier.csproj') -Raw
$notifier=Get-Content -LiteralPath (Join-Path $Root 'src\ITSeti.Maintenance.TicketNotifier\Program.cs') -Raw
$appStartup=Get-Content -LiteralPath (Join-Path $Root 'src\ITSeti.Maintenance.App\App.xaml.cs') -Raw
$mainWindow=Get-Content -LiteralPath (Join-Path $Root 'src\ITSeti.Maintenance.App\MainWindow.xaml.cs') -Raw
$server=Get-Content -LiteralPath (Join-Path (Split-Path $Root -Parent) 'ITSeti.Server\Program.cs') -Raw
if($serviceProject -notmatch '<Version>2\.3\.6</Version>' -or
   !$protocol.Contains('ITSetiMaintenanceService') -or !$protocol.Contains('ITSeti.Maintenance.Service.v1') -or
   !$coordinator.Contains('BuiltinUsersSid') -or !$coordinator.Contains('PipeAccessRights.ReadWrite') -or
   !$coordinator.Contains('ResolveOperation') -or $coordinator.Contains('ProcessStartInfo(request') -or
   !$client.Contains('NamedPipeClientStream') -or !$client.Contains('CancelAfter(TimeSpan.FromSeconds(3))')) {
    throw 'The service IPC contract is missing its version, ACL, timeout, or fixed operation allowlist.'
}
foreach($operation in @('FullCheck','QuickCheck','DailySmartCheck','ScheduledFullCheck','Cleanup','Repair','OrganizationSetup','UploadReports','RefreshSoftwareCatalog','ProbeTemperature','DisableWindowsUpdates','RestoreWindowsUpdates')) {
    if(!$coordinator.Contains('MaintenanceServiceOperation.'+$operation)){throw "Service operation is missing: $operation"}
}
if(!$coordinator.Contains('request.Operation == MaintenanceServiceOperation.QuickCheck') -or
   !$coordinator.Contains('MaintenanceServiceOperation.FullCheck : request.Operation.Value') -or
   !$coordinator.Contains('MaintenanceServiceOperation.QuickCheck => ("InstalledCheck.ps1", [])')) {
    throw 'Legacy quick-check requests must be normalized to the single full diagnostic operation.'
}
if(!$protocol.Contains('OpenSystemTool') -or !$protocol.Contains('OpenUtility') -or
   !$client.Contains('LaunchSystemToolAsync') -or !$client.Contains('LaunchUtilityAsync') -or
   !$coordinator.Contains('LaunchInteractiveTool') -or !$coordinator.Contains('ElevatedInteractiveProcessLauncher') -or
   !$coordinator.Contains('SessionId') -or !$coordinator.Contains('IsAdministrativeClient') -or
   !$coordinator.Contains('IsAdministratorOnlyLaunch')) { throw 'Privileged engineering-menu IPC is missing.' }
if(!$coordinator.Contains('"HWiNFO"') -or !$coordinator.Contains('FindHwInfoExecutable') -or !$coordinator.Contains('HWiNFO32.exe')) { throw 'HWiNFO is not exposed as an installed engineering-menu utility.' }
if(!$coordinator.Contains('MonitoringQueue') -or !$coordinator.Contains('CriticalMonitoringEvaluator') -or
   !$coordinator.Contains('DiskReadLatencyMs') -or !$coordinator.Contains('DiskWriteLatencyMs') -or
   !$coordinator.Contains('HwInfoReader.SessionRestartDue') -or !$coordinator.Contains('HwInfoReader.ResetSession') -or
   !$coordinator.Contains('TimeSpan.FromSeconds(5)')) {
    throw 'Continuous critical monitoring is missing disk latency or its protected queue.'
}
if (!$diagnostics.Contains('HWiNFO is owned by the SYSTEM service') -or $diagnostics.Contains('HwInfoReader.ReadCached(TimeSpan.FromMinutes(2)) ?? await HwInfoReader.ReadAsync')) {
    throw 'Interactive diagnostics must consume the SYSTEM HWiNFO cache instead of launching the sensor tool.'
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
if(!$coordinator.Contains('TicketUpdateMonitor') -or !$coordinator.Contains('PollTicketUpdatesAsync') -or
   !$ticketMonitor.Contains('api/v1/ticket-updates') -or !$ticketMonitor.Contains('ticket-updates.json') -or
   !$ticketMonitor.Contains('X-Support-Key') -or !$ticketMonitor.Contains('TakeLast(500)') -or
   !$server.Contains('if (!since.HasValue) { cursor = serverTime; cursorId = Guid.Empty; }')) {
    throw 'Service-owned ticket polling, durable deduplicated queue, or silent initial cursor is missing.'
}
if(!$notifierProject.Contains('<UseWindowsForms>true</UseWindowsForms>') -or
   !$notifier.Contains('ShowBalloonTip') -or !$notifier.Contains('Local\ITSeti-Maintenance-TicketNotifier-') -or
   !$install.Contains("ITSeti-Maintenance-Tickets") -or !$install.Contains('TicketNotifier.exe') -or
   !$appStartup.Contains('StartTicketNotifier()') -or $mainWindow.Contains('TicketMessagePoller') -or
   $mainWindow.Contains('Closing +=') -or !$builder.Contains('dotnet publish $notifierProject') -or
   !$installer.Contains('{#PackageRoot}\Notifier\*') -or
   !$uninstall.Contains("'ITSeti-Maintenance-Tickets'")) {
    throw 'Ticket notifications must survive main-window exit through the separate per-user notifier.'
}
if($coordinator.Contains('attemptedScheduledDate') -or $coordinator.Contains('last-full-run.txt')) {throw 'The service must not race the gated SYSTEM scheduled full-check task.'}
if($coordinator.Contains('attemptedScheduledDate') -or $coordinator.Contains('last-full-run.txt')) {throw 'The service must not race the gated SYSTEM scheduled full-check task.'}
'PASS: service IPC, allowlisted operations, HWiNFO session reset, package, install and uninstall contracts'
