param([switch]$StartRepair,[switch]$SmartOnly,[switch]$UserApproved)
$ErrorActionPreference='Stop'
$base=Join-Path $env:ProgramData 'ITSeti\Maintenance'
$runs=Join-Path $base 'Runs'
$tools=Join-Path (Split-Path $PSScriptRoot -Parent) 'Tools'
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
if($identity.User -and $identity.User.Value -eq 'S-1-5-18'){
    $passwordPolicy=Join-Path $PSScriptRoot 'Set-LocalPasswordsNeverExpire.ps1'
    if(Test-Path -LiteralPath $passwordPolicy){try{& $passwordPolicy -LogPath (Join-Path $base 'local-password-policy.log')}catch{[IO.File]::AppendAllText((Join-Path $base 'local-password-policy.log'),([DateTimeOffset]::UtcNow.ToString('O')+' ERROR '+$_.Exception.GetType().Name+[Environment]::NewLine),[Text.Encoding]::UTF8)}}
}
. (Join-Path $PSScriptRoot 'ScheduledFull.ps1')
if($StartRepair){
    $identity=[Security.Principal.WindowsIdentity]::GetCurrent()
    if(!$identity.User -or $identity.User.Value -ne 'S-1-5-18'){throw 'Planned checks must run as SYSTEM.'}
    $probe=Join-Path $base 'schedule-self-test.request'
    if(Test-Path -LiteralPath $probe -PathType Leaf){
        $token=[IO.File]::ReadAllText($probe).Trim()
        $probeId=[guid]::Empty
        if(![guid]::TryParseExact($token,'N',[ref]$probeId)){throw 'Invalid schedule self-test request.'}
        [IO.File]::WriteAllText((Join-Path $base 'schedule-self-test.result'),$token,[Text.Encoding]::ASCII)
        Remove-Item -LiteralPath $probe -Force
        exit 0
    }
    [IO.File]::WriteAllText((Join-Path $base 'scheduled-last-invoked.txt'),[DateTimeOffset]::UtcNow.ToString('O'),[Text.Encoding]::ASCII)
    if(!$UserApproved -and (Get-Date).Hour -ge 8){exit 0}
}
if($StartRepair -and !(Test-ScheduledFullDue $base)){exit 0}
$run=Join-Path $runs ([guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $run -Force | Out-Null
trap {
    [IO.File]::WriteAllText((Join-Path $run 'error.txt'), $_.Exception.ToString(), [Text.Encoding]::UTF8)
    if($StartRepair){
        [IO.File]::WriteAllText((Join-Path $base 'pending-auto-full-maintenance.txt'),(Split-Path $run -Leaf),[Text.Encoding]::ASCII)
        [IO.File]::WriteAllText((Join-Path $base 'scheduled-last-error.txt'),([DateTimeOffset]::UtcNow.ToString('O')+[Environment]::NewLine+$_.Exception.ToString()),[Text.Encoding]::UTF8)
    }
    exit 1
}
if($StartRepair){[IO.File]::WriteAllText((Join-Path $base 'scheduled-last-started.txt'),[DateTimeOffset]::UtcNow.ToString('O'),[Text.Encoding]::ASCII)}
$cancelRequest=Join-Path $run 'cancel-request.txt'
[IO.File]::WriteAllText($cancelRequest,'',[Text.Encoding]::ASCII)
if(!$StartRepair){
    & icacls.exe $cancelRequest /grant '*S-1-5-32-545:M' | Out-Null
    if($LASTEXITCODE -ne 0){throw 'Could not allow the current user to stop the check.'}
}
$latestName=if($SmartOnly){'latest-smart.txt'}elseif($StartRepair){'latest-auto-full.txt'}else{'latest-full.txt'}
$pending=Join-Path $base ($latestName+'.pending')
[IO.File]::WriteAllText($pending,$run,[Text.Encoding]::UTF8)
Move-Item -LiteralPath $pending -Destination (Join-Path $base $latestName) -Force
[IO.File]::WriteAllText((Join-Path $base 'latest.txt'),$run,[Text.Encoding]::UTF8)
function ConvertTo-UtcDateTimeOffset($Value) {
    if($Value -is [DateTimeOffset]){return $Value.ToUniversalTime()}
    if($Value -is [DateTime]){return [DateTimeOffset]$Value.ToUniversalTime()}
    $parsed=[DateTimeOffset]::MinValue
    if([DateTimeOffset]::TryParse([string]$Value,[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::AssumeUniversal,[ref]$parsed)){return $parsed.ToUniversalTime()}
    return $null
}
function Read-FreshTemperature([string]$Path,[int]$MaximumAgeSeconds) {
    if(!(Test-Path -LiteralPath $Path -PathType Leaf)){return $null}
    try {
        $stream=[IO.File]::Open($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]7)
        try {
            $reader=New-Object -TypeName IO.StreamReader -ArgumentList @($stream,[Text.Encoding]::UTF8,$true)
            try {$json=$reader.ReadToEnd()} finally {$reader.Dispose()}
        } finally {$stream.Dispose()}
        $reading=$json | ConvertFrom-Json
        $capturedAt=ConvertTo-UtcDateTimeOffset $reading.CapturedAtUtc
        if($null -eq $capturedAt){return $null}
        $age=([DateTimeOffset]::UtcNow-$capturedAt).TotalSeconds
        if($age -lt -60 -or $age -gt $MaximumAgeSeconds){return $null}
        if($null -ne $reading.TemperatureC) {
            $value=0.0
            if(![double]::TryParse([string]$reading.TemperatureC,[Globalization.NumberStyles]::Float,[Globalization.CultureInfo]::InvariantCulture,[ref]$value) -or $value -lt 5 -or $value -gt 120){return $null}
        }
        if([string]::IsNullOrWhiteSpace([string]$reading.Status)){return $null}
        return $reading
    } catch {return $null}
}
$sharedTemperatureFile=Join-Path $base 'cpu-temperature.json'
$cpuTemperatureFile=Join-Path $run 'cpu-temperature.json'
$sensor=Read-FreshTemperature $sharedTemperatureFile 90
$sensorStatus='Служба HWiNFO не вернула свежий результат датчика CPU.'
if($null -eq $sensor) {
    $sensor=[pscustomobject]@{TemperatureC=$null;Status=$sensorStatus;CapturedAtUtc=[DateTimeOffset]::UtcNow.ToString('O')}
}
[IO.File]::WriteAllText($cpuTemperatureFile,($sensor | ConvertTo-Json -Compress),[Text.Encoding]::UTF8)
$source=$PSScriptRoot
if(!$SmartOnly){
    & (Join-Path $source 'Upload-Reports.ps1') -RefreshPolicyOnly
}
foreach($file in @('FullCheckWorker.ps1','HeadlessDiskWorker.ps1','ResourceSampler.ps1','HWiNFO.ps1','Summary.ps1','DiskTools.ps1','Runtime.ps1','EventWorker.ps1','Repair.ps1','RepairWorker.ps1','NativeDiskMark.cs','allowed-processes.json','allowed-processes-win7.txt','allowed-publishers.txt','Set-WindowsAutomaticUpdates.ps1','Update-Application.ps1')) {
    Copy-Item -LiteralPath (Join-Path $source $file) -Destination $run -ErrorAction Stop
}
$env:ITSETI_CPU_TEMPERATURE_FILE=$cpuTemperatureFile
# Do not pass named PowerShell parameters through an untyped string array here.
# Windows PowerShell can bind the first array item as the positional RunRoot
# value, making '-RunRoot' reach Resolve-Path as the path itself.
$worker=[scriptblock]::Create([IO.File]::ReadAllText((Join-Path $run 'FullCheckWorker.ps1'),[Text.Encoding]::UTF8))
# Installed checks are always launched by SYSTEM (the service or the SYSTEM task).
# Keep SMART and DiskSpd enabled for user-requested checks without asking for UAC.
if($SmartOnly){
    & $worker -RunRoot $run -ToolsRoot $tools -HeadlessDiskSpd -TrustedTools -SkipResourceSampling -SkipDiskBenchmark
} else {
    & $worker -RunRoot $run -ToolsRoot $tools -HeadlessDiskSpd -TrustedTools
}
Remove-Item Env:ITSETI_CPU_TEMPERATURE_FILE -ErrorAction SilentlyContinue
if((Test-Path -LiteralPath $cancelRequest) -and [IO.File]::ReadAllText($cancelRequest).Trim() -eq 'cancel'){
    [IO.File]::WriteAllText((Join-Path $run 'cancelled.txt'),'cancelled',[Text.Encoding]::ASCII)
    exit 0
}
if(Test-Path -LiteralPath (Join-Path $run 'result.json')){
    if($SmartOnly){
        [IO.File]::WriteAllText((Join-Path $base 'last-smart-run.txt'),[DateTimeOffset]::UtcNow.ToString('O'),[Text.Encoding]::ASCII)
    }
    if($StartRepair){
        [IO.File]::WriteAllText((Join-Path $run 'stage.txt'),'Плановое восстановление Windows',[Text.Encoding]::UTF8)
        Complete-ScheduledFullCheck $base $run (Join-Path $source 'InstalledRepair.ps1')
        [IO.File]::WriteAllText((Join-Path $run 'stage.txt'),'Плановое восстановление завершено',[Text.Encoding]::UTF8)
        try {
            [IO.File]::WriteAllText((Join-Path $run 'stage.txt'),'Очистка системных обновлений и пакетов драйверов',[Text.Encoding]::UTF8)
            $script:Admin=$true
            $script:CompactOutput=$true
            function Section([string]$Text) { [IO.File]::WriteAllText((Join-Path $run 'stage.txt'),$Text,[Text.Encoding]::UTF8) }
            . ([scriptblock]::Create([IO.File]::ReadAllText((Join-Path $source 'Cleanup.ps1'),[Text.Encoding]::UTF8)))
            Invoke-Cleanup
            $selectedText=if($script:CleanupResult.SelectedCategories.Count){$script:CleanupResult.SelectedCategories -join ', '}else{'нет'}
            $unavailableText=if($script:CleanupResult.UnavailableCategories.Count){$script:CleanupResult.UnavailableCategories -join ', '}else{'нет'}
            [IO.File]::WriteAllText((Join-Path $run 'cleanup-after-repair.txt'),
                ('Профиль вошедшего пользователя не запускался: плановая задача работает от SYSTEM. Системные категории: {0}; отсутствуют в этой Windows: {1}; код {2}; изменение свободного места {3:N2} ГБ (включая параллельные изменения).' -f $selectedText,$unavailableText,$script:CleanupResult.ExitCode,$script:CleanupResult.ChangeGB),[Text.Encoding]::UTF8)
        } catch {
            [IO.File]::WriteAllText((Join-Path $run 'cleanup-after-repair.txt'),('Системная очистка не выполнена: '+$_.Exception.Message),[Text.Encoding]::UTF8)
        }
        Remove-Item -LiteralPath (Join-Path $base 'pending-auto-full-maintenance.txt') -Force -ErrorAction SilentlyContinue
    }
    elseif(!$SmartOnly){
        Set-ScheduledFullBaseline $base ([DateTimeOffset]::UtcNow)
        Remove-Item -LiteralPath (Join-Path $base 'pending-auto-full-maintenance.txt') -Force -ErrorAction SilentlyContinue
    }
    $scheduler=Join-Path $env:WINDIR 'System32\schtasks.exe'
    & $scheduler /Run /TN 'ITSeti-Maintenance-Update' | Out-Null
    if($LASTEXITCODE -ne 0){[IO.File]::WriteAllText((Join-Path $run 'update-error.txt'),('Update task could not start: '+$LASTEXITCODE),[Text.Encoding]::ASCII)}
} elseif($StartRepair) {
    throw 'Scheduled full diagnostic did not produce a result.'
}
if(Test-Path -LiteralPath (Join-Path $run 'cancelled.txt')){exit 0}
[IO.File]::WriteAllText((Join-Path $run 'completed.txt'),'completed',[Text.Encoding]::ASCII)
if($StartRepair){
    [IO.File]::WriteAllText((Join-Path $base 'scheduled-last-success.txt'),[DateTimeOffset]::UtcNow.ToString('O'),[Text.Encoding]::ASCII)
    Remove-Item -LiteralPath (Join-Path $base 'scheduled-last-error.txt') -Force -ErrorAction SilentlyContinue
}
