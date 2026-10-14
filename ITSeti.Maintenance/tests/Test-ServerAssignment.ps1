param([string]$Root = (Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference = 'Stop'

$installer = [IO.File]::ReadAllText((Join-Path $Root 'Installer.iss'))
$win7Installer = [IO.File]::ReadAllText((Join-Path $Root 'Installer-Win7.iss'))
$appXaml = [IO.File]::ReadAllText((Join-Path $Root 'src\ITSeti.Maintenance.App\MainWindow.xaml'))
$appCode = [IO.File]::ReadAllText((Join-Path $Root 'src\ITSeti.Maintenance.App\MainWindow.xaml.cs'))
$client = [IO.File]::ReadAllText((Join-Path $Root 'src\ITSeti.Maintenance.App\ServerEnrollmentClient.cs'))
$dialog = [IO.File]::ReadAllText((Join-Path $Root 'src\ITSeti.Maintenance.App\ServerAssignmentDialog.xaml.cs'))
$win7Dialog = [IO.File]::ReadAllText((Join-Path $Root 'src\ITSeti.Maintenance.Win7.Port\Win7ServerEnrollmentDialog.cs'))

foreach ($setup in @($installer, $win7Installer)) {
    if ($setup -match 'Connect-Server\.ps1' -or $setup -match 'SkipIfConnected' -or $setup -match 'Пароль подключения') {
        throw 'Setup must not package or launch the removed PowerShell enrollment prompt.'
    }
}
if ($appXaml -notmatch 'Click="OpenServerAssignment_Click"' -or
    $appCode -notmatch 'TryAutomaticServerEnrollmentAsync' -or
    $client -notmatch 'api/v1/auto-enroll' -or $client -notmatch 'api/v1/enroll' -or
    $dialog -notmatch 'ServerAssignmentDialog' -or $dialog -notmatch 'sites.Count == 2' -or
    $win7Dialog -notmatch 'TryAutomaticEnrollmentAsync') {
    throw 'Passwordless in-app IP/company/site enrollment is incomplete.'
}

$serverRoot = Split-Path $Root -Parent
$serverProgram = [IO.File]::ReadAllText((Join-Path $serverRoot 'ITSeti.Server\Program.cs'))
$database = [IO.File]::ReadAllText((Join-Path $serverRoot 'ITSeti.Server\Database.cs'))
$serverHtml = [IO.File]::ReadAllText((Join-Path $serverRoot 'ITSeti.Server\wwwroot\index.html'))
$serverJs = [IO.File]::ReadAllText((Join-Path $serverRoot 'ITSeti.Server\wwwroot\app.js'))
if ($serverProgram -notmatch 'MapGet\("/api/v1/catalog"' -or
    $serverProgram -notmatch 'MapPost\("/api/v1/enroll"' -or
    $serverProgram -notmatch 'MapPost\("/api/v1/auto-enroll"' -or
    $serverProgram -notmatch 'AutomaticIpEnrollment\.ResolveUnique' -or
    $serverProgram -notmatch 'clientIp is null' -or
    $serverProgram -notmatch 'AutomaticIpEnrollment\.CanReuseExisting\(null, clientIp\)') {
    throw 'Server passwordless catalog/enrollment/IP reuse routes are incomplete.'
}
if ($serverHtml -notmatch 'id="device-company"' -or $serverHtml -notmatch 'id="device-site"' -or
    $serverHtml -match 'id="device-pc"' -or $serverJs -notmatch '/computer-tree' -or
    $serverJs -match 'device\.name|alert\.computer|ticket\.computer') {
    throw 'The server computer view must navigate by company, optional object and serial-identified PC.'
}
if ($database -notmatch 'DROP INDEX IF EXISTS ix_devices_hardware_fingerprint' -or
    $database -notmatch 'UPDATE ticket_requests child' -or $database -notmatch 'UPDATE check_runs child') {
    throw 'Startup migration must re-key duplicate serials and preserve their report/ticket history.'
}
'PASS: passwordless in-app enrollment, IP reuse and serial-based server identity contracts'
