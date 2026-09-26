param([switch]$StartRepair,[switch]$Quick)
$ErrorActionPreference='Stop'
$base=Join-Path $env:ProgramData 'ITSeti\Maintenance'
$runs=Join-Path $base 'Runs'
$tools=Join-Path (Split-Path $PSScriptRoot -Parent) 'Tools'
$run=Join-Path $runs ([guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $run -Force | Out-Null
trap {
    [IO.File]::WriteAllText((Join-Path $run 'error.txt'), $_.Exception.ToString(), [Text.Encoding]::UTF8)
    exit 1
}
$pending=Join-Path $base 'latest.pending.txt'
[IO.File]::WriteAllText($pending,$run,[Text.Encoding]::UTF8)
Move-Item -LiteralPath $pending -Destination (Join-Path $base 'latest.txt') -Force
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
$sensor=Read-FreshTemperature $sharedTemperatureFile 45
if($null -eq $sensor) {
    $probeStartedAt=[DateTimeOffset]::UtcNow
    try {
        $scheduler=Join-Path $env:WINDIR 'System32\schtasks.exe'
        $probeProcess=Start-Process -FilePath $scheduler -ArgumentList @('/Run','/TN','ITSeti-Maintenance-Temperature') -PassThru -WindowStyle Hidden -ErrorAction Stop
        if(!$probeProcess.WaitForExit(5000)){
            try {$probeProcess.Kill()} catch {}
            throw 'Запрос датчика не ответил за 5 секунд.'
        }
        if($probeProcess.ExitCode -ne 0){throw "Задача датчика завершилась с кодом $($probeProcess.ExitCode)."}
        $deadline=[DateTimeOffset]::UtcNow.AddSeconds(8)
        do {
            Start-Sleep -Milliseconds 200
            $candidate=Read-FreshTemperature $sharedTemperatureFile 300
            if($candidate) {
                $candidateTime=ConvertTo-UtcDateTimeOffset $candidate.CapturedAtUtc
                if($candidateTime -ge $probeStartedAt.AddSeconds(-1)){$sensor=$candidate;break}
            }
        } while([DateTimeOffset]::UtcNow -lt $deadline)
        if($null -eq $sensor){throw 'Системная задача не вернула свежий результат датчика за 8 секунд.'}
    } catch {$sensorStatus='Не удалось получить температуру CPU: '+$_.Exception.Message}
}
if($null -eq $sensor) {
    $sensor=[pscustomobject]@{TemperatureC=$null;Status=$sensorStatus;CapturedAtUtc=[DateTimeOffset]::UtcNow.ToString('O')}
}
[IO.File]::WriteAllText($cpuTemperatureFile,($sensor | ConvertTo-Json -Compress),[Text.Encoding]::UTF8)
$source=$PSScriptRoot
foreach($file in @('FullCheckWorker.ps1','HeadlessDiskWorker.ps1','ResourceSampler.ps1','Summary.ps1','DiskTools.ps1','Runtime.ps1','EventWorker.ps1','Repair.ps1','RepairWorker.ps1','NativeDiskMark.cs','allowed-processes.json','allowed-processes-win7.txt','allowed-publishers.txt','Set-WindowsAutomaticUpdates.ps1','Update-Application.ps1')) {
    Copy-Item -LiteralPath (Join-Path $source $file) -Destination $run -ErrorAction Stop
}
$env:ITSETI_CPU_TEMPERATURE_FILE=$cpuTemperatureFile
& ([scriptblock]::Create([IO.File]::ReadAllText((Join-Path $run 'FullCheckWorker.ps1'),[Text.Encoding]::UTF8))) -RunRoot $run -ToolsRoot $tools -HeadlessDiskSpd -TrustedTools -StartRepair:$StartRepair -SkipResourceSampling:$Quick -SkipDiskBenchmark:$Quick
Remove-Item Env:ITSETI_CPU_TEMPERATURE_FILE -ErrorAction SilentlyContinue
if($Quick -and (Test-Path -LiteralPath (Join-Path $run 'result.json'))){
    [IO.File]::WriteAllText((Join-Path $base 'last-quick-run.txt'),[DateTimeOffset]::UtcNow.ToString('O'),[Text.Encoding]::ASCII)
}
