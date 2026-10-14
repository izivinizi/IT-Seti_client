param([string]$Root=(Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference='Stop'
$serviceProject=Get-Content -LiteralPath (Join-Path $Root 'src\ITSeti.Maintenance.Service\ITSeti.Maintenance.Service.csproj') -Raw
$coordinator=[IO.File]::ReadAllText((Join-Path $Root 'src\ITSeti.Maintenance.Service\MaintenanceServiceCoordinator.cs'),[Text.Encoding]::UTF8)
$authorization=Get-Content -LiteralPath (Join-Path $Root 'src\ITSeti.Maintenance.Service\ServiceClientAuthorization.cs') -Raw
$engineerAuth=Get-Content -LiteralPath (Join-Path $Root 'src\ITSeti.Maintenance.Service\EngineerAuthorization.cs') -Raw
$installedCheck=Get-Content -LiteralPath (Join-Path $Root 'src\ITSeti.Maintenance.Infrastructure\Backend\InstalledCheck.ps1') -Raw
$scheduledFull=Get-Content -LiteralPath (Join-Path $Root 'src\ITSeti.Maintenance.Infrastructure\Backend\ScheduledFull.ps1') -Raw
$fullWorker=[IO.File]::ReadAllText((Join-Path $Root 'src\ITSeti.Maintenance.Infrastructure\Backend\FullCheckWorker.ps1'),[Text.Encoding]::UTF8)
$protocol=Get-Content -LiteralPath (Join-Path $Root 'src\ITSeti.Maintenance.Infrastructure\MaintenanceServiceProtocol.cs') -Raw
$client=Get-Content -LiteralPath (Join-Path $Root 'src\ITSeti.Maintenance.Infrastructure\MaintenanceServiceClient.cs') -Raw
$diagnostics=Get-Content -LiteralPath (Join-Path $Root 'src\ITSeti.Maintenance.Infrastructure\WindowsDiagnosticsRunner.cs') -Raw
$install=Get-Content -LiteralPath (Join-Path $Root 'Install-Maintenance.ps1') -Raw
$uninstall=Get-Content -LiteralPath (Join-Path $Root 'Uninstall-Maintenance.ps1') -Raw
$builder=Get-Content -LiteralPath (Join-Path $Root 'Build-InstallPackage.ps1') -Raw
$installer=Get-Content -LiteralPath (Join-Path $Root 'Installer.iss') -Raw
$ticketMonitor=Get-Content -LiteralPath (Join-Path $Root 'src\ITSeti.Maintenance.Infrastructure\TicketUpdateMonitor.cs') -Raw
$wtsNotification=Get-Content -LiteralPath (Join-Path $Root 'src\ITSeti.Maintenance.Service\WtsTicketNotification.cs') -Raw
$appStartup=Get-Content -LiteralPath (Join-Path $Root 'src\ITSeti.Maintenance.App\App.xaml.cs') -Raw
$mainWindow=Get-Content -LiteralPath (Join-Path $Root 'src\ITSeti.Maintenance.App\MainWindow.xaml.cs') -Raw
$organizationRunner=Get-Content -LiteralPath (Join-Path $Root 'src\ITSeti.Maintenance.Infrastructure\OrganizationSetupRunner.cs') -Raw
$server=Get-Content -LiteralPath (Join-Path (Split-Path $Root -Parent) 'ITSeti.Server\Program.cs') -Raw
if($serviceProject -notmatch '<Version>2\.4\.7</Version>' -or
   !$protocol.Contains('ITSetiMaintenanceService') -or !$protocol.Contains('ITSeti.Maintenance.Service.v1') -or
   !$coordinator.Contains('BuiltinUsersSid') -or !$coordinator.Contains('PipeAccessRights.ReadWrite') -or
   !$coordinator.Contains('ResolveOperation') -or $coordinator.Contains('ProcessStartInfo(request') -or
   !$client.Contains('NamedPipeClientStream') -or !$client.Contains('CancelAfter(TimeSpan.FromSeconds(3))')) {
    throw 'The service IPC contract is missing its version, ACL, timeout, or fixed operation allowlist.'
}
if(!$coordinator.Contains('ReadHwInfoWithTimeoutAsync') -or
   !$coordinator.Contains('TimeSpan.FromSeconds(10)') -or
   !$coordinator.Contains('Task.Run(() => HwInfoReader.ReadAsync(CancellationToken.None))') -or
   !$coordinator.Contains('catch (Exception ex)')) {
    throw 'A stalled HWiNFO read must not stop the service monitoring loop.'
}
foreach($operation in @('FullCheck','QuickCheck','DailySmartCheck','ScheduledFullCheck','Cleanup','Repair','OrganizationSetup','UploadReports','RefreshSoftwareCatalog','ProbeTemperature','DisableWindowsUpdates','RestoreWindowsUpdates','DisableWindowsDefender','EnableWindowsDefender','OpenServerPackage')) {
    if(!$coordinator.Contains('MaintenanceServiceOperation.'+$operation)){throw "Service operation is missing: $operation"}
}
if(!$protocol.Contains('OpenServerPackage') -or !$client.Contains('LaunchServerPackageAsync') -or
   !$coordinator.Contains('ValidateInteractivePackage') -or !$coordinator.Contains('DownloadAndVerifyPackageAsync') -or
   !$coordinator.Contains('SHA256.HashDataAsync')) {
    throw 'Interactive server packages must be catalog-bound, SHA-256 verified and engineer-authorized.'
}
if(!$coordinator.Contains('request.Operation == MaintenanceServiceOperation.QuickCheck') -or
   !$coordinator.Contains('MaintenanceServiceOperation.FullCheck : request.Operation.Value') -or
   !$coordinator.Contains('MaintenanceServiceOperation.QuickCheck => ("InstalledCheck.ps1", [])')) {
    throw 'Legacy quick-check requests must be normalized to the single full diagnostic operation.'
}
if(!$protocol.Contains('OpenSystemTool') -or !$protocol.Contains('OpenUtility') -or
   !$client.Contains('LaunchSystemToolAsync') -or !$client.Contains('LaunchUtilityAsync') -or
   !$coordinator.Contains('LaunchInteractiveTool') -or !$coordinator.Contains('ElevatedInteractiveProcessLauncher') -or
   !$coordinator.Contains('request.SessionId != client.SessionId') -or
   !$coordinator.Contains('ServiceClientAuthorization.Identify(pipe)') -or
   !$authorization.Contains('GetNamedPipeClientProcessId') -or
   !$engineerAuth.Contains('Rfc2898DeriveBytes.Pbkdf2') -or
   !$coordinator.Contains('Authenticate(request.Password, client)') -or
   !$coordinator.Contains('IsAuthorized(request.AuthorizationToken, client)') -or
   $protocol.Contains('EngineerPassword') -or $client.Contains('EngineerPassword')) { throw 'Privileged engineering-menu IPC identity checks are missing.' }
if(!$coordinator.Contains('"HWiNFO"') -or !$coordinator.Contains('FindHwInfoExecutable') -or !$coordinator.Contains('HWiNFO32.exe')) { throw 'HWiNFO is not exposed as an installed engineering-menu utility.' }
if(!$coordinator.Contains('MonitoringQueue') -or !$coordinator.Contains('CriticalMonitoringEvaluator') -or
   !$coordinator.Contains('DiskReadLatencyMs') -or !$coordinator.Contains('DiskWriteLatencyMs') -or
   !$coordinator.Contains('HwInfoReader.SessionRestartDue') -or !$coordinator.Contains('HwInfoReader.ResetSession') -or
   !$coordinator.Contains('TimeSpan.FromSeconds(5)') -or
   !$coordinator.Contains('snapshot is null || liveSnapshotTimedOut') -or
   !$coordinator.Contains('monitoring.Observe(CriticalMonitoringEvaluator.Evaluate(snapshot), snapshot.StartedAt)')) {
    throw 'Continuous monitoring must ignore cached data after a timeout and confirm new readings over time.'
}
$failureDiagnostics=@('STATUS_ACCESS_VIOLATION','process.StartInfo.FileName','BuildOperationFailureStatus',
    'STDERR (последние строки):','STDOUT (последние строки):','Windows identity=','каталог существует=',
    'DescribeFileSystemSecurity','права каталога:','HRESULT=0x','атомарная замена целевого файла') |
    Where-Object {!$coordinator.Contains($_)}
if($failureDiagnostics){throw ('Background operation diagnostics are missing: '+($failureDiagnostics -join ', '))}
if(!$organizationRunner.Contains('EngineerAuthorizationRequiredException') -or
   !$mainWindow.Contains('ex is not EngineerAuthorizationRequiredException')) {
    throw 'Expected rejection without engineer authorization must stay a user-facing message, not an application-error event.'
}
if (!$diagnostics.Contains('HWiNFO is owned by the SYSTEM service') -or $diagnostics.Contains('HwInfoReader.ReadCached(TimeSpan.FromMinutes(2)) ?? await HwInfoReader.ReadAsync')) {
    throw 'Interactive diagnostics must consume the SYSTEM HWiNFO cache instead of launching the sensor tool.'
}
if(!$coordinator.Contains('MaintenanceServiceOperation.Cleanup => (Folder: "CleanupRequests", Pattern: "*.request")') -or
   !$coordinator.Contains('if (failure is null && HasPendingRequests(operation))')) {
    throw 'Cleanup requests arriving during an active service run must be picked up by a follow-up run.'
}
if($installedCheck.Contains("/Run /TN 'ITSeti-Maintenance-Upload'") -or
   !$coordinator.Contains('TryStartReportUpload();') -or
   !$coordinator.Contains('operations.Enqueue(operation, OperationGroup(operation)')) {
    throw 'Check and cleanup reports must be uploaded through the service queue, without an overlapping scheduled-task launch.'
}
if(!$install.Contains("`$serviceName='ITSetiMaintenanceService'") -or !$install.Contains('New-Service -Name $serviceName') -or
   !$install.Contains('Grant-SystemTreeFullControl $securityRoot') -or
   !$install.Contains('start= auto') -or $install.Contains('start= delayed-auto') -or
   !$install.Contains('DelayedAutoStart') -or !$install.Contains('Start-Service -Name $serviceName') -or
   !$uninstall.Contains("`$serviceName='ITSetiMaintenanceService'") -or !$uninstall.Contains('sc.exe delete $serviceName') -or
   !$builder.Contains('dotnet publish $serviceProject') -or !$installer.Contains('{#PackageRoot}\Service\*')) {
    throw 'Build, install, update, or uninstall integration for the service is incomplete.'
}
if(!$coordinator.Contains('TicketUpdateMonitor') -or !$coordinator.Contains('PollTicketUpdatesAsync') -or
   !$ticketMonitor.Contains('api/v1/ticket-updates') -or !$ticketMonitor.Contains('ticket-updates.json') -or
   !$coordinator.Contains('refresh-ticket-updates') -or !$client.Contains('RefreshTicketUpdatesAsync') -or
   !$coordinator.Contains('ticketPollRequested = true') -or !$coordinator.Contains('operation == MaintenanceServiceOperation.OrganizationSetup') -or
   !$ticketMonitor.Contains('Timeout.InfiniteTimeSpan') -or
   !$ticketMonitor.Contains('X-Support-Key') -or !$ticketMonitor.Contains('TakeLast(500)') -or
   !$server.Contains('if (!since.HasValue) { cursor = serverTime; cursorId = Guid.Empty; }')) {
    throw 'Service-owned ticket polling, durable deduplicated queue, or silent initial cursor is missing.'
}
if(!$ticketMonitor.Contains('onIncomingMessages?.Invoke(incoming)') -or
   !$coordinator.Contains('new TicketUpdateMonitor(onIncomingMessages: WtsTicketNotification.Show)') -or
   !$wtsNotification.Contains('WTSEnumerateSessionsW') -or !$wtsNotification.Contains('WTSSendMessageW') -or
   !$wtsNotification.Contains('WtsActive') -or !$wtsNotification.Contains('NotificationTimeoutSeconds = 30') -or
   !$wtsNotification.Contains('NotificationTimeoutSeconds, out _, false') -or
   !$ticketMonitor.Contains('TimeSpan.FromSeconds(30)') -or
   !$install.Contains("Remove-ItemProperty -Path `$runKey -Name 'ITSeti-Maintenance-Tickets'") -or
   !$install.Contains("Remove-Item -LiteralPath (Join-Path `$InstallRoot 'Notifier')") -or
   $install.Contains("Set-ItemProperty -Path `$runKey -Name 'ITSeti-Maintenance-Tickets'") -or
   $appStartup.Contains('StartTicketNotifier') -or $mainWindow.Contains('TicketMessagePoller') -or
   $mainWindow.Contains('Closing +=') -or $builder.Contains('dotnet publish $notifierProject') -or
   $installer.Contains('{#PackageRoot}\Notifier\*') -or
   !$uninstall.Contains("'ITSeti-Maintenance-Tickets'")) {
    throw 'Ticket polling must suspend when idle and service notifications must target active user sessions without a notifier process.'
}
if($coordinator.Contains('attemptedScheduledDate') -or $coordinator.Contains('last-full-run.txt')) {throw 'The service must not race the gated SYSTEM scheduled full-check task.'}
if($coordinator.Contains('attemptedScheduledDate') -or $coordinator.Contains('last-full-run.txt')) {throw 'The service must not race the gated SYSTEM scheduled full-check task.'}
if($coordinator.Contains('nextSmartAttempt') -or $coordinator.Contains('HasFreshSmartReport') -or
   $coordinator.Contains('StartOperation(MaintenanceServiceOperation.DailySmartCheck)')) {
    throw 'The service must not start an unattended hourly smart diagnostic.'
}
$ordinaryOperation=([regex]::Match($authorization,'public static bool OrdinaryOperation\(MaintenanceServiceOperation operation\) =>(?s:.*?)\n\s*\}')).Value
if(!$ordinaryOperation -or $ordinaryOperation.Contains('DailySmartCheck')) {
    throw 'An ordinary client request must not invoke the legacy background quick-check operation.'
}
if(!$scheduledFull.Contains('[TimeSpan]::FromDays(10)') -or
   !$coordinator.Contains('operationTriggers') -or !$coordinator.Contains('requestingSessions') -or
   !$coordinator.Contains('ITSETI_REQUESTED_SESSION_ID') -or
   !$fullWorker.Contains('Trigger=[string]$env:ITSETI_CHECK_TRIGGER') -or
   !$fullWorker.Contains('RequestedByWindowsSessionId=') -or
   !$fullWorker.Contains('WindowsSessionId=[Diagnostics.Process]::GetCurrentProcess().SessionId') -or
   !$coordinator.Contains('Trigger = "Служба непрерывно контролирует свежие показания датчиков и нагрузки"')) {
    throw 'Reports must retain the 10-day full-check gate and identify their trigger and Windows session.'
}
'PASS: service IPC, 10-day full check, no unattended hourly quick check, report trigger/session, HWiNFO, package, install and uninstall contracts'
