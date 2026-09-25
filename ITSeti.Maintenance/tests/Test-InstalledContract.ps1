param([string]$Root)
$ErrorActionPreference='Stop'
$worker=Join-Path $Root 'src\ITSeti.Maintenance.Infrastructure\Backend\HeadlessDiskWorker.ps1'
$source=[IO.File]::ReadAllText($worker,[Text.Encoding]::UTF8)
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseInput($source,[ref]$tokens,[ref]$errors)
if($errors){throw 'Headless worker has PowerShell syntax errors.'}
$function=@($ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Read-Speed'},$true))
if($function.Count -ne 1){throw 'Read-Speed was not found.'}
. ([scriptblock]::Create($function[0].Extent.Text))
$warningFunction=@($ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Test-PrivilegeWarningOnly'},$true))
if($warningFunction.Count -ne 1){throw 'DiskSpd warning classifier was not found.'}
. ([scriptblock]::Create($warningFunction[0].Extent.Text))
$exitFunction=@($ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Test-DiskSpdExit'},$true))
if($exitFunction.Count -ne 1){throw 'DiskSpd exit classifier was not found.'}
. ([scriptblock]::Create($exitFunction[0].Extent.Text))
$fixture=Join-Path $Root 'tests\diskspd-sample.xml'
if((Read-Speed $fixture 'ReadBytes') -ne 100 -or (Read-Speed $fixture 'WriteBytes') -ne 50){throw 'DiskSpd XML speed calculation failed.'}
$sample=[IO.File]::ReadAllText($fixture)
$realStyle=Join-Path $env:TEMP ('diskspd-output-'+[guid]::NewGuid().ToString('N')+'.txt')
try {
    [IO.File]::WriteAllText($realStyle,$sample+'Score: 0'+"`r`naverageLatency: 0.000000")
    if((Read-Speed $realStyle 'ReadBytes') -ne 100){throw 'DiskSpd trailing statistics broke XML parsing.'}
} finally {Remove-Item -LiteralPath $realStyle -ErrorAction SilentlyContinue}
$privilegeWarning="WARNING: Error adjusting token privileges for SeManageVolumePrivilege (error code: 1300)`nWARNING: Could not set privileges for setting valid file size; will use a slower method of preparing the file"
if(!(Test-PrivilegeWarningOnly $privilegeWarning) -or (Test-PrivilegeWarningOnly ($privilegeWarning+"`nERROR: access denied"))){throw 'DiskSpd warning classification failed.'}
$rejectsMissingExit=!(Test-DiskSpdExit $null '')
$rejectsError=!(Test-DiskSpdExit $null 'ERROR: access denied')
$rejectsFailure=!(Test-DiskSpdExit 5 '')
$acceptsSuccess=Test-DiskSpdExit 0 ''
$acceptsPrivilegeWarningAtZero=Test-DiskSpdExit 0 $privilegeWarning
$rejectsErrorAtZero=!(Test-DiskSpdExit 0 'ERROR: access denied')
$acceptsWarning=Test-DiskSpdExit 1 $privilegeWarning
if(!$rejectsMissingExit -or !$rejectsError -or !$rejectsFailure -or !$acceptsSuccess -or !$acceptsPrivilegeWarningAtZero -or !$rejectsErrorAtZero -or !$acceptsWarning){
    throw 'DiskSpd blank exit code or stderr handling failed.'
}
$sampler=Get-Content -LiteralPath (Join-Path $Root 'src\ITSeti.Maintenance.Infrastructure\Backend\ResourceSampler.ps1') -Raw
$samplerTokens=$null;$samplerErrors=$null
$samplerAst=[Management.Automation.Language.Parser]::ParseInput($sampler,[ref]$samplerTokens,[ref]$samplerErrors)
if($samplerErrors){throw 'Resource sampler has PowerShell syntax errors.'}
if($sampler -notmatch 'resource-sample-progress\.json') {throw 'Live resource samples are not published during the 30-second check.'}
$sampleRoot=[IO.Path]::GetFullPath((Join-Path $env:TEMP ('ITSeti-sampler-'+[guid]::NewGuid().ToString('N'))))
$tempPrefix=[IO.Path]::GetFullPath($env:TEMP).TrimEnd('\')+'\'
if(!$sampleRoot.StartsWith($tempPrefix,[StringComparison]::OrdinalIgnoreCase)){throw 'Sampler test directory escaped the temporary folder.'}
New-Item -ItemType Directory -Path $sampleRoot -Force | Out-Null
try {
    & ([scriptblock]::Create($sampler)) -RunRoot $sampleRoot -Seconds 0 -Interval 1
    foreach($name in @('resource-sample-progress.json','resource-sample.json')) {
        $sample=Get-Content -LiteralPath (Join-Path $sampleRoot $name) -Raw | ConvertFrom-Json
        if($sample.Samples -ne 1 -or $sample.Error){throw "Sampler did not produce a valid $name"}
    }
} finally {Remove-Item -LiteralPath $sampleRoot -Recurse -Force -ErrorAction SilentlyContinue}
$counterFunction=@($samplerAst.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'CounterDelta'},$true))
if($counterFunction.Count -ne 1){throw 'Resource counter delta helper was not found.'}
. ([scriptblock]::Create($counterFunction[0].Extent.Text))
if((CounterDelta 20 10) -ne 10 -or $null -ne (CounterDelta 10 20)){throw 'Reset performance counters must be ignored, not treated as 32-bit rollover.'}
$install=[IO.File]::ReadAllText((Join-Path $Root 'Install-Maintenance.ps1'),[Text.Encoding]::UTF8)
if($install -notmatch 'Register-ScheduledTask' -or $install -notmatch "-User 'SYSTEM'" -or $install -notmatch 'GRGX;;;BU' -or $install -notmatch 'icacls.exe') {throw 'Installed task privilege boundary is missing.'}
if(!$install.Contains('install.log') -or !$install.Contains('install-status.txt') -or !$install.Contains('$taskErrors')) {throw 'Installation failures must be logged, and unavailable tasks must not abort app setup.'}
$bootstrap=[IO.File]::ReadAllText((Join-Path $Root 'Install-ITSeti.ps1'),[Text.Encoding]::UTF8)
if($bootstrap -match 'LoadUserProfile' -or !$bootstrap.Contains('NativeErrorCode') -or !$bootstrap.Contains("Get-Service -Name 'seclogon'")) {throw 'Credential bootstrap must avoid loading an unnecessary admin profile and report Windows logon errors.'}
if($install -notmatch 'ITSeti-Maintenance-Quick' -or $install -notmatch 'CurrentVersion\\Run') {throw 'Quick-check startup is missing.'}
$entry=[IO.File]::ReadAllText((Join-Path $Root 'src\ITSeti.Maintenance.Infrastructure\Backend\InstalledCheck.ps1'),[Text.Encoding]::UTF8)
if(!$install.Contains('ITSeti-Maintenance-QuickFull') -or !$install.Contains('-Quick')) {throw 'Installed quick full-check task is missing.'}
if($install -match 'DaysInterval 14' -or !$install.Contains('ITSeti-Maintenance-AutoFullRepair') -or $install -notmatch 'DaysInterval 60') {throw 'The 14-day check must be prompted; the 60-day full repair schedule must remain.'}
$appSource=[IO.File]::ReadAllText((Join-Path $Root 'src\ITSeti.Maintenance.App\App.xaml.cs'),[Text.Encoding]::UTF8)
$promptSource=[IO.File]::ReadAllText((Join-Path $Root 'src\ITSeti.Maintenance.App\ScheduledCheckPrompt.cs'),[Text.Encoding]::UTF8)
if(!$appSource.Contains('last-quick-run.txt') -or !$appSource.Contains('TimeSpan.FromDays(14)') -or
   !$promptSource.Contains('DialogResult = false') -or !$appSource.Contains('AddDays(1)') -or !$appSource.Contains('ScheduleReminder')) {throw 'The prompted 14-day check or one-day reminder is incomplete.'}
if(!$install.Contains('ITSeti-Maintenance-DisableUpdates') -or !$install.Contains('ITSeti-Maintenance-RestoreUpdates')) {throw 'Windows Update policy tasks are missing.'}
$updater=Join-Path $Root 'src\ITSeti.Maintenance.Infrastructure\Backend\Update-Application.ps1'
$updaterTokens=$null;$updaterErrors=$null
[void][Management.Automation.Language.Parser]::ParseFile($updater,[ref]$updaterTokens,[ref]$updaterErrors)
if($updaterErrors){throw 'Application updater has PowerShell syntax errors.'}
$updaterSource=[IO.File]::ReadAllText($updater,[Text.Encoding]::UTF8)
if(!$install.Contains('ITSeti-Maintenance-Update') -or !$install.Contains('Update-Application.ps1') -or
   !$updaterSource.Contains('S-1-5-18') -or !$updaterSource.Contains('Get-FileHash') -or
   !$updaterSource.Contains('ITSeti-Maintenance-Setup.exe') -or !$updaterSource.Contains('ITSeti.Maintenance.dll') -or
   !$updaterSource.Contains('Split-Path $PSScriptRoot -Parent') -or $updaterSource -match 'runas') {
    throw 'Application update task must be fixed, SYSTEM-only, and validate release SHA-256.'
}
$releaseClient=Get-Content -LiteralPath (Join-Path $Root 'src\ITSeti.Maintenance.Infrastructure\GitHubReleaseClient.cs') -Raw
$releaseRunner=Get-Content -LiteralPath (Join-Path $Root 'src\ITSeti.Maintenance.Infrastructure\ApplicationUpdateRunner.cs') -Raw
if($releaseClient -notmatch 'api\.github\.com/repos/izivinizi/IT-Seti_client/releases/latest' -or
   $releaseClient -notmatch 'sha256:' -or $releaseRunner -notmatch 'ITSeti-Maintenance-Update' -or $releaseRunner -match '"runas"') {
    throw 'GitHub release check or no-UAC task launcher is missing.'
}
$updatePolicy=Join-Path $Root 'src\ITSeti.Maintenance.Infrastructure\Backend\Set-WindowsAutomaticUpdates.ps1'
$updateTokens=$null;$updateErrors=$null
[void][Management.Automation.Language.Parser]::ParseFile($updatePolicy,[ref]$updateTokens,[ref]$updateErrors)
if($updateErrors){throw 'Windows Update policy worker has PowerShell syntax errors.'}
$updateSource=[IO.File]::ReadAllText($updatePolicy,[Text.Encoding]::UTF8)
if($updateSource -notmatch 'original-policy\.json' -or $updateSource -notmatch 'ChangedExternally' -or $updateSource -notmatch 'NoAutoUpdate') {throw 'Windows Update policy worker must preserve and protect the existing setting.'}
if(!$entry.Contains('[switch]$Quick') -or !$entry.Contains('SkipResourceSampling:$Quick') -or !$entry.Contains('SkipDiskBenchmark:$Quick')) {throw 'Quick check must retain SMART while skipping long measurements.'}
if($install -notmatch 'ITSeti-Maintenance-Repair' -or $install -notmatch 'InstalledRepair.ps1') {throw 'Independent repair task is missing.'}
if($install -notmatch 'ITSeti-Maintenance-Cleanup' -or $install -notmatch 'InstalledCleanup.ps1' -or $install -notmatch 'CleanupRuns') {throw 'Installed cleanup task or protected results directory is missing.'}
$cleanupEntry=Join-Path $Root 'src\ITSeti.Maintenance.Infrastructure\Backend\InstalledCleanup.ps1'
$cleanupTokens=$null;$cleanupErrors=$null
[void][Management.Automation.Language.Parser]::ParseFile($cleanupEntry,[ref]$cleanupTokens,[ref]$cleanupErrors)
if($cleanupErrors){throw 'Installed cleanup worker has PowerShell syntax errors.'}
$cleanupSource=Get-Content -LiteralPath $cleanupEntry -Raw
if($cleanupSource -notmatch 'TrustedScriptsRoot \$PSScriptRoot' -or $cleanupSource -notmatch 'StatusRoot \$run') {throw 'SYSTEM cleanup must run trusted scripts and write protected results.'}
$adminCleanup=Get-Content -LiteralPath (Join-Path $Root 'src\ITSeti.Maintenance.Infrastructure\Backend\AppCleanupWorker.ps1') -Raw
if($adminCleanup -match "Join-Path \`$JobRoot 'Cleanup.ps1'" -or $adminCleanup -match "Join-Path \`$JobRoot 'UserCleanup.ps1'") {throw 'SYSTEM cleanup must not execute user-controlled scripts.'}
$cleanupRunner=Get-Content -LiteralPath (Join-Path $Root 'src\ITSeti.Maintenance.Infrastructure\UserCleanupRunner.cs') -Raw
if($cleanupRunner -notmatch 'ITSeti-Maintenance-Cleanup' -or $cleanupRunner -match '"runas"') {throw 'Installed cleanup must not prompt for admin credentials.'}
if($cleanupRunner -notmatch 'finally\s*\{' -or $cleanupRunner -notmatch 'SignalUserAsync\(root, "abort"\)' -or
   $cleanupRunner -notmatch 'File\.Move\(temporary, destination\)' -or $cleanupRunner -notmatch 'or TimeoutException') {
    throw 'Cleanup worker must receive an atomic abort signal on failed startup and handle timeouts.'
}
$repairEntry=Join-Path $Root 'src\ITSeti.Maintenance.Infrastructure\Backend\InstalledRepair.ps1'
$repairTokens=$null;$repairErrors=$null
[void][Management.Automation.Language.Parser]::ParseFile($repairEntry,[ref]$repairTokens,[ref]$repairErrors)
if($repairErrors){throw 'Installed repair worker has PowerShell syntax errors.'}
if((Get-Content -LiteralPath $repairEntry -Raw) -notmatch 'finished.txt') {throw 'Installed repair has no completion marker.'}
if($entry -notmatch "ITSeti\\Maintenance" -or $entry -notmatch 'HeadlessDiskSpd' -or $entry -notmatch "'ResourceSampler.ps1'") {throw 'Installed worker is missing a fixed path or resource sampler.'}
'PASS: DiskSpd XML parser, Windows Update policy safety, 14/60-day schedules, and installed-task contract; no privileged task created.'
