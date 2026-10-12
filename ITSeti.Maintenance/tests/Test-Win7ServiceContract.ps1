param([string]$Root = (Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference = 'Stop'
$service = Get-Content -LiteralPath (Join-Path $Root 'src\ITSeti.Maintenance.Win7.Port\Win7MaintenanceService.cs') -Raw
$app = Get-Content -LiteralPath (Join-Path $Root 'src\ITSeti.Maintenance.Win7.Port\App.xaml.cs') -Raw
$project = Get-Content -LiteralPath (Join-Path $Root 'src\ITSeti.Maintenance.Win7.Port\ITSeti.Maintenance.Win7.Port.csproj') -Raw
$installer = Get-Content -LiteralPath (Join-Path $Root 'Installer-Win7.iss') -Raw
if ($project -notmatch '<Version>2\.3\.6-beta</Version>' -or
    $installer -notmatch 'AppVersion=2\.3\.6' -or
    !$service.Contains('ServiceBase.Run') -or !$service.Contains('ITSetiMaintenanceWin7') -or
    !$service.Contains('ITSeti.Maintenance.Win7.v1') -or !$service.Contains('LocalSystemSid') -or
    !$service.Contains('AuthenticatedUserSid') -or !$service.Contains('CollectWithCleanup') -or
    !$service.Contains('CollectMonitoring') -or !$service.Contains('ServerReportUploader.Upload') -or
    !$service.Contains('Win7InteractiveProcessLauncher.LaunchAsSystem') -or !$service.Contains('LaunchSystemToolAsync') -or
    !$service.Contains('RunId') -or
    !$service.Contains('LegacyHwInfoReader.Apply') -or
    !$project.Contains('LegacyHwInfoReader.cs') -or
    !$app.Contains('"--service"')) {
    throw 'Win7 service host is missing its SYSTEM runtime, IPC, unified maintenance or startup contract.'
}
if ($installer -notmatch 'create ITSetiMaintenanceWin7' -or
    $installer -notmatch 'start ITSetiMaintenanceWin7' -or
    $installer -notmatch 'delete ITSetiMaintenanceWin7') {
    throw 'Win7 installer does not install, start and remove the maintenance service.'
}
if ($service -notmatch 'string.Equals\(operation, "quick"[^\r\n]+StartRun\(true\)' -or
    $service -match 'StartRun\(false\)' -or
    $project -match 'CanRunQuickFull') {
    throw 'Legacy quick-check requests must resolve to the single full check.'
}
'PASS: Win7 SYSTEM service, IPC, unified maintenance and installer contract'
