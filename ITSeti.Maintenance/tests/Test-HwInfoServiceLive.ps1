param([int]$WaitSeconds = 0)
$ErrorActionPreference = 'Stop'
if ($WaitSeconds -lt 0 -or $WaitSeconds -gt 300) { throw 'WaitSeconds must be between 0 and 300.' }

$service = Get-CimInstance Win32_Service -Filter "Name='ITSetiMaintenanceService'"
if (!$service -or $service.State -ne 'Running' -or $service.ProcessId -le 0) {
    throw 'ITSetiMaintenanceService is not running.'
}
$servicePath = $service.PathName.Trim('"')
$version = (Get-Item -LiteralPath $servicePath).VersionInfo.FileVersion
if ([version]$version -lt [version]'2.4.2.0') { throw "Installed service is $version; 2.4.2 or newer is required." }

$cachePath = Join-Path $env:ProgramData 'ITSeti\Maintenance\hwinfo.json'
$deadline = (Get-Date).AddSeconds($WaitSeconds)
do {
    $sensorProcess = Get-CimInstance Win32_Process -Filter "Name='HWiNFO32.exe'" |
        Where-Object { $_.ParentProcessId -eq $service.ProcessId } | Select-Object -First 1
    $cacheFile = Get-Item -LiteralPath $cachePath -ErrorAction SilentlyContinue
    $report = $null
    if ($cacheFile) {
        try { $report = Get-Content -LiteralPath $cachePath -Raw | ConvertFrom-Json }
        catch { $report = $null }
    }
    $captured = [DateTimeOffset]::MinValue
    if ($report -and $report.CapturedAtUtc) {
        try {
            $captured = if ($report.CapturedAtUtc -is [DateTime]) {
                [DateTimeOffset]$report.CapturedAtUtc
            } else {
                [DateTimeOffset]::Parse([string]$report.CapturedAtUtc,
                    [Globalization.CultureInfo]::InvariantCulture,
                    [Globalization.DateTimeStyles]::RoundtripKind)
            }
        }
        catch { $captured = [DateTimeOffset]::MinValue }
    }
    $age = [DateTimeOffset]::UtcNow - $captured
    $fresh = $sensorProcess -and $report -and $cacheFile -and
        $cacheFile.LastWriteTimeUtc -gt [DateTime]::UtcNow.AddSeconds(-90) -and
        $age.TotalSeconds -ge -60 -and $age.TotalSeconds -le 90 -and
        $report.Source -eq 'HWiNFO Shared Memory' -and
        $report.Freshness -eq 'SourceTimestampChecked' -and
        $report.Sensors.Count -gt 0 -and
        $report.CpuTemperatureC -ge 5 -and $report.CpuTemperatureC -le 120 -and
        $report.CpuLoadPercent -ge 0 -and $report.CpuLoadPercent -le 100
    if ($fresh) {
        'PASS HWiNFO service live: service={0}, sensorPid={1}, age={2:N0}s, sensors={3}, CPU={4:N1} C, load={5:N1}%, 12V={6}, 5V={7}, 3.3V={8}' -f
            $version, $sensorProcess.ProcessId, $age.TotalSeconds, $report.Sensors.Count,
            $report.CpuTemperatureC, $report.CpuLoadPercent, $report.Rail12V, $report.Rail5V, $report.Rail3V
        exit 0
    }
    if ((Get-Date) -ge $deadline) { break }
    Start-Sleep -Seconds 2
} while ($true)

$errorPath = Join-Path $env:ProgramData 'ITSeti\Maintenance\hwinfo-error.txt'
$detail = if (Test-Path -LiteralPath $errorPath) { (Get-Content -LiteralPath $errorPath -Raw).Trim() } else { 'no HWiNFO error file' }
throw "HWiNFO service reading is unavailable or stale. $detail"
