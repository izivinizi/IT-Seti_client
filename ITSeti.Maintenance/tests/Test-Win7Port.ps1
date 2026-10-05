param([string]$Root = (Split-Path $PSScriptRoot -Parent), [switch]$SkipUi)
$ErrorActionPreference = 'Stop'
$exe = Join-Path $Root 'src\ITSeti.Maintenance.Win7.Port\bin\Release\net48\ITSeti.Maintenance.Win7.Port.exe'
if (!(Test-Path -LiteralPath $exe -PathType Leaf)) { throw 'Build the Win7 port in Release first.' }
$output = Join-Path $Root ('artifacts\win7-port-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $output -Force | Out-Null
$report = Join-Path $output 'diagnostic.json'
$process = Start-Process -FilePath $exe -ArgumentList @('--diagnose', ('"' + $report + '"')) -Wait -PassThru
if ($process.ExitCode -ne 0 -or !(Test-Path -LiteralPath $report)) { throw 'Win7 diagnostic worker failed.' }
$snapshot = Get-Content -LiteralPath $report -Raw -Encoding UTF8 | ConvertFrom-Json
if (!$snapshot.ComputerName -or !@($snapshot.Disks).Count -or !$snapshot.CheckedAt -or
    !$snapshot.Id -or !$snapshot.StartedAt -or $null -eq $snapshot.AdminAccounts -or
    $null -eq $snapshot.CpuPercent -or $snapshot.CpuPercent -lt 0 -or $snapshot.CpuPercent -gt 100 -or
    $null -eq $snapshot.ActiveUptimeHours -or $snapshot.ActiveUptimeHours -lt 0) {
    throw 'Win7 diagnostic result is incomplete.'
}
$startedAt = [DateTimeOffset]::MinValue
if ([guid]::Empty -eq [guid]$snapshot.Id -or ![DateTimeOffset]::TryParse($snapshot.StartedAt, [ref]$startedAt)) {
    throw 'Win7 diagnostic report id or timestamp is invalid.'
}

Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase
$assembly = [Reflection.Assembly]::LoadFrom($exe)
$adminAudit = $assembly.GetType('ITSeti.Maintenance.App.LegacyAdminAccountAudit',$true)
$allowedAdmin = $adminAudit.GetMethod('IsAllowedLocalAccount',[Reflection.BindingFlags]::Static -bor [Reflection.BindingFlags]::NonPublic)
foreach($name in @('Admin','it-seti','Administrator',([string][char]0x410+[char]0x434+[char]0x43C+[char]0x438+[char]0x43D+[char]0x438+[char]0x441+[char]0x442+[char]0x440+[char]0x430+[char]0x442+[char]0x43E+[char]0x440))){
    if(!$allowedAdmin.Invoke($null,@($env:COMPUTERNAME+'\'+$name))){throw 'Designated Win7 administrator incorrectly flagged.'}
}
if($allowedAdmin.Invoke($null,@('OTHER-DOMAIN\Administrator'))){throw 'Win7 local admin exception must not hide domain members.'}
$scheduled = $assembly.GetType('ITSeti.Maintenance.Win7.ScheduledCheckRunner',$true)
$fullDue = $scheduled.GetMethod('IsFullDue',[Reflection.BindingFlags]::Static -bor [Reflection.BindingFlags]::NonPublic)
$dueRoot = Join-Path $output 'full-due-fixture'
New-Item -ItemType Directory -Path $dueRoot -Force | Out-Null
$now = [DateTime]::UtcNow
$dueArgs = [object[]]@($now.PSObject.BaseObject,$dueRoot.ToString())
if($fullDue.Invoke($null,$dueArgs)){throw 'Missing baseline must not trigger immediate full maintenance.'}
foreach($days in @(0,59,60)){
    [IO.File]::WriteAllText((Join-Path $dueRoot 'full-check-baseline.txt'),$now.AddDays(-$days).ToString('o'))
    if([bool]$fullDue.Invoke($null,$dueArgs) -ne ($days -ge 60)){throw 'Win7 60-day full maintenance gate failed.'}
}
[IO.File]::WriteAllText((Join-Path $dueRoot 'full-check-baseline.txt'),$now.ToString('o'))
[IO.File]::WriteAllText((Join-Path $dueRoot 'scheduled-full-failure.txt'),'failure')
if(!$fullDue.Invoke($null,$dueArgs)){throw 'Failed nightly full maintenance must remain eligible for retry.'}
$taskInstaller = $assembly.GetType('ITSeti.Maintenance.Win7.SmartTaskInstaller', $true)
$isMissing = $taskInstaller.GetMethod('IsMissingTask', [Reflection.BindingFlags]::Static -bor [Reflection.BindingFlags]::NonPublic)
$scheduler = New-Object -ComObject Schedule.Service
$scheduler.Connect()
$missingTaskException = $null
try { $scheduler.GetFolder('\').GetTask('ITSeti-Test-Missing-Task-240104') | Out-Null }
catch { $missingTaskException = $_.Exception }
if (!$missingTaskException -or !$isMissing.Invoke($null, @($missingTaskException))) {
    throw 'Missing Scheduled Task COM error is not treated as an absent task.'
}
$diagnostics = $assembly.GetType('ITSeti.Maintenance.Win7.LegacyDiagnostics', $true)
$cancellation = $assembly.GetType('ITSeti.Maintenance.Win7.ManualTaskCancellation', $true)
$ownerGone = $cancellation.GetMethod('OwnerGone', [Reflection.BindingFlags]::Static -bor [Reflection.BindingFlags]::NonPublic)
$self = [Diagnostics.Process]::GetCurrentProcess()
$alive = "owner:$($self.Id):$($self.StartTime.ToUniversalTime().Ticks)"
if ($ownerGone.Invoke($null, @($alive)) -or !$ownerGone.Invoke($null, @('owner:2147483647:1'))) {
    throw 'Win7 forced-close ownership detection failed.'
}
$collect = $diagnostics.GetMethod('Collect', [Reflection.BindingFlags]::Public -bor [Reflection.BindingFlags]::Static)
$cancel = New-Object System.Threading.CancellationTokenSource
$cancel.Cancel()
try {
    $null = $collect.Invoke($null, @($false, $cancel.Token, $false))
    throw 'A pre-cancelled Win7 check collected data.'
} catch {
    $errorType = $_.Exception
    while ($errorType.InnerException) { $errorType = $errorType.InnerException }
    if ($errorType -isnot [OperationCanceledException]) { throw }
}
$updater = $assembly.GetType('ITSeti.Maintenance.App.Win7GitHubUpdater', $true)
$flags = [Reflection.BindingFlags]::Static -bor [Reflection.BindingFlags]::NonPublic
$parse = $updater.GetMethod('ParseManifest', $flags)
if (!$parse) { throw 'Win7 manifest parser is missing.' }
$version = '1.2.3'
$url = "https://github.com/izivinizi/IT-Seti_client/releases/download/v$version-win7-beta/ITSeti-Maintenance-Win7-Setup.exe"
$manifest = [string]([ordered]@{
    version = $version; tag = "v$version-win7-beta"; assetName = 'ITSeti-Maintenance-Win7-Setup.exe'
    size = 12345; digest = ('sha256:' + ('a' * 64)); downloadUrl = $url
} | ConvertTo-Json -Compress)
$current = [version]'1.2.2'
if (!$parse.Invoke($null, @($manifest, $current))) { throw 'A newer Win7 release was not offered.' }
if ($parse.Invoke($null, @($manifest, [version]$version))) { throw 'The installed Win7 release was offered again.' }
foreach ($bad in @($manifest.Replace($url, 'https://example.org/setup.exe'),
                  $manifest.Replace(('a' * 64), 'invalid'))) {
    try {
        $null = $parse.Invoke($null, @($bad, $current))
        throw 'An invalid Win7 release manifest was accepted.'
    } catch {
        $errorType = $_.Exception
        while ($errorType.InnerException) { $errorType = $errorType.InnerException }
        if ($errorType -isnot [IO.InvalidDataException]) { throw }
    }
}

$scheduledType = $assembly.GetType('ITSeti.Maintenance.Win7.ScheduledCheckRunner', $true)
$scheduledFlags = [Reflection.BindingFlags]::Static -bor [Reflection.BindingFlags]::NonPublic
if ($scheduledType.GetField('TaskName', $scheduledFlags).GetRawConstantValue() -ne 'ITSeti-Maintenance-Win7-Scheduled' -or
    $scheduledType.GetField('FullTaskName', $scheduledFlags).GetRawConstantValue() -ne 'ITSeti-Maintenance-Win7-AutoFull') {
    throw 'Win7 scheduled task names are incorrect.'
}
$source = Get-Content -LiteralPath (Join-Path $Root 'src\ITSeti.Maintenance.Win7\SmartTaskInstaller.cs') -Raw
if ($source -notmatch 'ScheduledCheckRunner\.TaskName[^\r\n]+,\s*14\)' -or
    $source -notmatch 'ScheduledCheckRunner\.FullTaskName[^\r\n]+,\s*1\)' -or
    $source -notmatch 'StartWhenAvailable = name != ScheduledCheckRunner.FullTaskName' -or
    $source -notmatch 'ITSeti-Maintenance-Win7-Upload[^\r\n]+--upload-reports' -or
    $source -notmatch 'ITSeti-Maintenance-Win7-Update[^\r\n]+--update-application') {
    throw '14/60-day checks, report upload and application update tasks are not registered.'
}
$portProject = Get-Content -LiteralPath (Join-Path $Root 'src\ITSeti.Maintenance.Win7.Port\ITSeti.Maintenance.Win7.Port.csproj') -Raw
if ($portProject -match 'SupportDialog\.xaml|MyTicketsDialog\.xaml') { throw 'Ticket creation UI must remain excluded from Windows 7.' }
$packageBuilder = Get-Content -LiteralPath (Join-Path $Root 'Build-Win7Package.ps1') -Raw
$installerSource = Get-Content -LiteralPath (Join-Path $Root 'Installer-Win7.iss') -Raw
if ($packageBuilder -notmatch 'CrystalDiskMark9' -or $installerSource -notmatch 'Tools\\CrystalDiskMark9') {
    throw 'CrystalDiskMark is missing from the Win7 package or installer.'
}
$benchmarkSource = Get-Content -LiteralPath (Join-Path $Root 'src\ITSeti.Maintenance.Win7\BenchmarkTaskRunner.cs') -Raw
if ($benchmarkSource -notmatch 'TargetVolume = systemDisk' -or $benchmarkSource -notmatch 'TargetDiskModel = systemDisk') {
    throw 'DiskSpd report must name the actual system volume and its physical disk model.'
}
$scheduledSource = Get-Content -LiteralPath (Join-Path $Root 'src\ITSeti.Maintenance.Win7\ScheduledCheckRunner.cs') -Raw
if ($scheduledSource -notmatch 'RunWorker\(\)\s*\{\s*return RunWorker\(false\);\s*\}' -or
    $scheduledSource -notmatch 'LegacyDiagnostics\.Collect\(full,\s*scheduled:\s*true\)' -or
    $scheduledSource -notmatch 'LegacyMaintenance\.RunSfc\(\)' -or
    $scheduledSource.IndexOf('LegacyMaintenance.RunSfc()') -gt $scheduledSource.IndexOf('LegacyMaintenance.CleanupAfterFullCheck(')) {
    throw 'Scheduled quick/full Win7 work is not separated as expected.'
}
$cleanupSource = Get-Content -LiteralPath (Join-Path $Root 'src\ITSeti.Maintenance.Win7\LegacyMaintenance.cs') -Raw
$systemCleanupSource = Get-Content -LiteralPath (Join-Path $Root 'src\ITSeti.Maintenance.Win7\SystemCleanupTaskRunner.cs') -Raw
$diagnosticSource = Get-Content -LiteralPath (Join-Path $Root 'src\ITSeti.Maintenance.Win7\LegacyDiagnostics.cs') -Raw
$windowSource = Get-Content -LiteralPath (Join-Path $Root 'src\ITSeti.Maintenance.Win7.Port\MainWindow.xaml.cs') -Raw
if (!$cleanupSource.Contains('thumbcache_*.db') -or $cleanupSource -notmatch 'Temporary Internet Files' -or
    $cleanupSource -notmatch 'INetCache' -or $cleanupSource -notmatch 'D3DSCache' -or
    $cleanupSource -notmatch 'SHEmptyRecycleBin' -or
    $systemCleanupSource -notmatch 'Delivery Optimization Files' -or $systemCleanupSource -notmatch 'Update Cleanup' -or
    $systemCleanupSource -notmatch 'Device Driver Packages' -or
    $diagnosticSource -notmatch 'if \(cleanupAfterBenchmark\)' -or
    $windowSource -notmatch 'CollectWithCleanup\(full, cancellation\.Token, false, true, cleanupUserSid, progress\)') {
    throw 'Win7 cleanup must preserve user cache/thumbnails/recycle-bin and Windows update/driver categories for engineer checks.'
}
$workerResult = $updater.GetMethod('RunBackgroundWorker', [Reflection.BindingFlags]::Static -bor [Reflection.BindingFlags]::Public).Invoke($null, @())
if ($workerResult -ne 2) { throw 'The background updater worker ran without SYSTEM credentials.' }
$promptMethod = $scheduledType.GetMethods($scheduledFlags) | Where-Object {
    $_.Name -eq 'NeedsDaytimeFullFailurePrompt' -and $_.GetParameters().Count -eq 3
} | Select-Object -First 1
if (!$promptMethod) { throw 'Scheduled full failure prompt policy is missing.' }
$failurePath = Join-Path $output 'full-failure.txt'
$remindPath = Join-Path $output 'remind-after.txt'
[IO.File]::WriteAllText($failurePath, [DateTime]::Today.AddHours(3).ToUniversalTime().ToString('o'))
$atSeven = [DateTime]::Today.AddHours(7)
$atNine = [DateTime]::Today.AddHours(9)
$promptArguments = New-Object 'object[]' 3
$promptArguments[1] = [string]$failurePath
$promptArguments[2] = [string]$remindPath
$promptArguments[0] = [DateTime]$atSeven
if ($promptMethod.Invoke($null, $promptArguments)) { throw 'The full failure prompt appeared before daytime.' }
$promptArguments[0] = [DateTime]$atNine
if (!$promptMethod.Invoke($null, $promptArguments)) {
    throw 'Scheduled full failure notification timing is incorrect.'
}
[IO.File]::WriteAllText($remindPath, [DateTime]::UtcNow.AddDays(1).ToString('o'))
if ($promptMethod.Invoke($null, $promptArguments)) {
    throw 'A deferred scheduled full failure notification was shown again early.'
}
$backgroundWorker = $updater.GetMethod('RunBackgroundWorker', [Reflection.BindingFlags]::Static -bor [Reflection.BindingFlags]::Public)
$queueWorker = $updater.GetMethod('QueueBackgroundCheckAsync', [Reflection.BindingFlags]::Static -bor [Reflection.BindingFlags]::Public)
if (!$backgroundWorker -or !$queueWorker) { throw 'Background Win7 auto-update workflow is incomplete.' }
$manifestUrl = $updater.GetField('ManifestUrl', $flags).GetRawConstantValue()
if ($manifestUrl -ne 'https://raw.githubusercontent.com/izivinizi/IT-Seti_client/main/release-win7.json') {
    throw 'Win7 auto-updater does not read its manifest from main.'
}

if (!$SkipUi) {
    $app = New-Object ITSeti.Maintenance.App.App
    $app.InitializeComponent()
    $app.ShutdownMode = [System.Windows.ShutdownMode]::OnExplicitShutdown
    try {
        foreach ($mode in @($false, $true)) {
            $window = New-Object ITSeti.Maintenance.App.MainWindow -ArgumentList @($mode, $report)
            try {
                $window.Show()
                $window.UpdateLayout()
                $userShell = $window.FindName('UserShell')
                $adminShell = $window.FindName('AdminShell')
                if ($mode -and ($adminShell.Visibility -ne 'Visible' -or
                    $userShell.Visibility -ne 'Collapsed')) { throw 'Win7 engineer mode did not open.' }
                if (!$mode -and $userShell.Visibility -ne 'Visible') {
                    throw "Win7 user mode did not open (user=$($userShell.Visibility), admin=$($adminShell.Visibility))."
                }
                $view = $window.DataContext
                $stop = $window.FindName($(if ($mode) { 'AdminStopCheckButton' } else { 'UserStopCheckButton' }))
                if (!$stop -or $stop.Visibility -ne 'Collapsed') { throw 'Stop control was visible while idle.' }
                if (!(Test-Path -LiteralPath (Join-Path (Split-Path $exe -Parent) 'Tools\TreeSize\TreeSize.exe')) -and
                    $window.FindName('OpenLowSpaceScanButton').Visibility -ne 'Collapsed') {
                    throw 'The Win7 user overview shows a TreeSize action that is not in the installer.'
                }
                $view.Set('IsFullCheckRunning', $true)
                $view.Set('CanStopFullCheck', $true)
                $window.UpdateLayout()
                if ($stop.Visibility -ne 'Visible' -or !$stop.IsEnabled) { throw 'Stop control did not activate during a check.' }
                $stopBitmap = New-Object System.Windows.Media.Imaging.RenderTargetBitmap(
                    [int]$window.ActualWidth, [int]$window.ActualHeight, 96, 96,
                    [System.Windows.Media.PixelFormats]::Pbgra32)
                $stopBitmap.Render($window)
                $stopEncoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
                $stopEncoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($stopBitmap))
                $stopName = if ($mode) { 'admin-stop.png' } else { 'user-stop.png' }
                $stopStream = [IO.File]::Create((Join-Path $output $stopName))
                try { $stopEncoder.Save($stopStream) } finally { $stopStream.Dispose() }
                $view.Set('IsFullCheckRunning', $false)
                $view.Set('CanStopFullCheck', $false)
                $tabs = $window.FindName('AdminTabs')
                $navigation = $window.FindName('AdminNavigation')
                if ($mode) {
                    $view = $window.DataContext
                    $audit = [string]$view.Get('AdminAccountAuditStatus')
                    $summary = [string]$view.Get('OverviewStatus')
                    if ($audit.StartsWith('Посторонние администраторы:') -and
                        $summary -eq 'По доступным показателям замечаний нет') {
                        throw 'The Win7 overview hides the administrator warning.'
                    }
                }
                $indexes = if ($mode) { 0..($tabs.Items.Count - 1) } else { @(0) }
                foreach ($index in $indexes) {
                    if ($mode) { $tabs.SelectedIndex = $index; $navigation.SelectedIndex = $index }
                    $window.UpdateLayout()
                    $window.Dispatcher.Invoke([Action] {}, [System.Windows.Threading.DispatcherPriority]::ApplicationIdle)
                    $window.UpdateLayout()
                    $bitmap = New-Object System.Windows.Media.Imaging.RenderTargetBitmap(
                        [int]$window.ActualWidth, [int]$window.ActualHeight, 96, 96,
                        [System.Windows.Media.PixelFormats]::Pbgra32)
                    $bitmap.Render($window)
                    $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
                    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
                    $name = if ($mode) { "admin-tab-$index.png" } else { 'user.png' }
                    $stream = [IO.File]::Create((Join-Path $output $name))
                    try { $encoder.Save($stream) } finally { $stream.Dispose() }
                }
            } finally { $window.Close() }
        }
    } finally { $app.Shutdown() }
}
Write-Output "PASS: Win7 real diagnostic, release manifest validation and UI. $output"
