param([string]$Root = (Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference = 'Stop'
$connectPath = Join-Path $Root 'Connect-Server.ps1'
$connect = [IO.File]::ReadAllText($connectPath)
$tokens = $null
$errors = $null
[void][Management.Automation.Language.Parser]::ParseFile($connectPath, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw ($errors -join [Environment]::NewLine) }

if ($connect -match '\$script:companies\[\$companyBox\.SelectedIndex\]' -or
    $connect -notmatch '\$script:visibleCompanies\[\$companyBox\.SelectedIndex\]') {
    throw 'Filtered company selection must resolve to its matching catalog record.'
}
if ($connect -notmatch '\$script:visibleSites\[\$siteBox\.SelectedIndex\]') {
    throw 'Selected site must resolve from the filtered, company-specific list.'
}
if ($connect -notmatch 'companyBox\.Add_TextUpdate' -or $connect -notmatch 'siteBox\.Add_TextUpdate' -or
    $connect -notmatch '\$script:sites\s*=\s*@\(\$catalog\[''sites''\]\)') {
    throw 'Company/object catalog search is incomplete.'
}
if ($connect -match 'elseif\s*\(\$script:visibleCompanies\.Count\s*-gt\s*0\)\s*\{\s*\$companyBox\.SelectedIndex\s*=\s*0') {
    throw 'New device enrollment must not silently select the first company.'
}

$installer = [IO.File]::ReadAllText((Join-Path $Root 'Installer.iss'))
$win7Installer = [IO.File]::ReadAllText((Join-Path $Root 'Installer-Win7.iss'))
$appXaml = [IO.File]::ReadAllText((Join-Path $Root 'src\ITSeti.Maintenance.App\MainWindow.xaml'))
$appCode = [IO.File]::ReadAllText((Join-Path $Root 'src\ITSeti.Maintenance.App\MainWindow.xaml.cs'))
if ($installer -notmatch 'Source: "Connect-Server\.ps1"' -or
    $win7Installer -notmatch 'Source: "Connect-Server\.ps1"' -or
    $appXaml -notmatch 'Click="OpenServerAssignment_Click"') {
    throw 'The assignment script is not packaged or its engineer-mode button is not wired.'
}
foreach ($setup in @($installer, $win7Installer)) {
    if ($setup -notmatch 'if not WizardSilent then' -or $setup -notmatch 'SW_SHOWNORMAL' -or $setup -notmatch '-SkipIfConnected') {
        throw 'Setup must skip enrollment for a connected PC while retaining first-install assignment.'
    }
}
if ($connect -notmatch 'AssignmentConsole.*ShowWindow' -or
    $connect -notmatch 'form\.ShowDialog' -or
    $appCode -notmatch 'WindowStyle = ProcessWindowStyle\.Hidden') {
    throw 'Assignment must show its dialog without a PowerShell console.'
}

$serverRoot = Split-Path $Root -Parent
$serverProgram = [IO.File]::ReadAllText((Join-Path $serverRoot 'ITSeti.Server\Program.cs'))
$database = [IO.File]::ReadAllText((Join-Path $serverRoot 'ITSeti.Server\Database.cs'))
$serverHtml = [IO.File]::ReadAllText((Join-Path $serverRoot 'ITSeti.Server\wwwroot\index.html'))
$serverJs = [IO.File]::ReadAllText((Join-Path $serverRoot 'ITSeti.Server\wwwroot\app.js'))
if ($serverHtml -notmatch 'id="device-company"' -or $serverHtml -notmatch 'id="device-site"' -or
    $serverHtml -match 'id="device-pc"' -or $serverJs -notmatch '/computer-tree') {
    throw 'The server computer view must navigate by company, object and PC without displaying host names.'
}
if ($serverProgram -notmatch 'MapGet\("/device-options"' -or
    $serverProgram -notmatch 'fingerprint is null' -or
    $serverJs -match 'device\.name|alert\.computer|ticket\.computer') {
    throw 'PC selection and serial-based identification are not consistently wired through the server UI.'
}
if ($database -notmatch 'DROP INDEX IF EXISTS ix_devices_hardware_fingerprint' -or
    $database -notmatch 'SELECT id,serial_number FROM devices WHERE serial_number IS NOT NULL' -or
    $database -notmatch 'UPDATE ticket_requests child' -or $database -notmatch 'UPDATE check_runs child') {
    throw 'Startup migration must re-key duplicate serials and preserve their report/ticket history.'
}
Write-Output 'PASS: setup preserves enrollment, inline assignment search and serial-based server identity.'
