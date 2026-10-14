param(
    [string]$ExecutablePath = (Join-Path $env:ProgramFiles 'ITSeti Maintenance\ITSeti.Maintenance.exe'),
    [ValidateRange(1, 100)][int]$Cycles = 20,
    [ValidateRange(1, 120)][int]$StartupTimeoutSeconds = 20,
    [ValidateRange(1, 120)][int]$CloseTimeoutSeconds = 15,
    [ValidateRange(0, 600)][int]$ObservationSeconds = 0
)

$ErrorActionPreference = 'Stop'
$ExecutablePath = [IO.Path]::GetFullPath($ExecutablePath)
if (!(Test-Path -LiteralPath $ExecutablePath -PathType Leaf)) {
    throw "Installed application was not found: $ExecutablePath"
}

$applicationName = [IO.Path]::GetFileName($ExecutablePath)
$sessionId = (Get-Process -Id $PID).SessionId
$baselineIds = @(
    Get-CimInstance Win32_Process -Filter "Name='$applicationName'" -ErrorAction SilentlyContinue |
        Where-Object { $_.SessionId -eq $sessionId -and $_.ExecutablePath -eq $ExecutablePath } |
        ForEach-Object { [int]$_.ProcessId }
)
$previousDisableReportUpload = $env:ITSETI_DISABLE_REPORT_UPLOAD
$env:ITSETI_DISABLE_REPORT_UPLOAD = '1'
$records = New-Object 'System.Collections.Generic.List[object]'

try {
    for ($cycle = 1; $cycle -le $Cycles; $cycle++) {
        $process = Start-Process -FilePath $ExecutablePath -PassThru
        try {
            $startupDeadline = [DateTime]::UtcNow.AddSeconds($StartupTimeoutSeconds)
            do {
                $process.Refresh()
                if ($process.HasExited) {
                    throw "Cycle $($cycle): process exited before creating a window (code $($process.ExitCode))."
                }
                if ($process.MainWindowHandle -ne [IntPtr]::Zero) { break }
                Start-Sleep -Milliseconds 100
            } while ([DateTime]::UtcNow -lt $startupDeadline)

            if ($process.MainWindowHandle -eq [IntPtr]::Zero) {
                throw "Cycle $($cycle): the main window did not appear within $StartupTimeoutSeconds seconds."
            }

            $workingSetMiB = [Math]::Round($process.WorkingSet64 / 1MB, 1)
            if ($ObservationSeconds -gt 0) {
                Start-Sleep -Seconds $ObservationSeconds
                $process.Refresh()
                Write-Output ("OBSERVE cycle {0}: initial={1} MiB, after {2}s={3} MiB" -f
                    $cycle, $workingSetMiB, $ObservationSeconds, [Math]::Round($process.WorkingSet64 / 1MB, 1))
            }
            $closeStarted = [DateTime]::UtcNow
            if (!$process.CloseMainWindow()) {
                throw "Cycle $($cycle): the main window did not accept WM_CLOSE."
            }
            if (!$process.WaitForExit($CloseTimeoutSeconds * 1000)) {
                $children = Get-CimInstance Win32_Process -Filter "ParentProcessId=$($process.Id)" |
                    Select-Object Name, ProcessId
                throw "Cycle $($cycle): process stayed alive for $CloseTimeoutSeconds seconds after closing. Children: $($children | ConvertTo-Json -Compress)"
            }

            $closeSeconds = [Math]::Round(([DateTime]::UtcNow - $closeStarted).TotalSeconds, 2)
            $records.Add([pscustomobject]@{
                Cycle = $cycle
                ProcessId = $process.Id
                WorkingSetMiB = $workingSetMiB
                ExitCode = $process.ExitCode
                CloseSeconds = $closeSeconds
            })
            Write-Output ("PASS cycle {0}: pid={1}, memory={2} MiB, close={3}s" -f
                $cycle, $process.Id, $workingSetMiB, $closeSeconds)
        }
        catch {
            $process.Refresh()
            if (!$process.HasExited) {
                Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
                $process.WaitForExit(5000)
            }
            throw
        }
        finally {
            $process.Dispose()
        }
    }
}
finally {
    if ($null -eq $previousDisableReportUpload) {
        Remove-Item Env:ITSETI_DISABLE_REPORT_UPLOAD -ErrorAction SilentlyContinue
    }
    else {
        $env:ITSETI_DISABLE_REPORT_UPLOAD = $previousDisableReportUpload
    }
}

$remaining = @(
    Get-CimInstance Win32_Process -Filter "Name='$applicationName'" -ErrorAction SilentlyContinue |
        Where-Object {
            $_.SessionId -eq $sessionId -and $_.ExecutablePath -eq $ExecutablePath -and
            [int]$_.ProcessId -notin $baselineIds
        }
)
if ($remaining.Count -gt 0) {
    throw "Application processes started by the lifecycle test remain: $($remaining | Select-Object ProcessId,ParentProcessId | ConvertTo-Json -Compress)"
}

$memory = $records.WorkingSetMiB | Measure-Object -Minimum -Maximum -Average
$close = $records.CloseSeconds | Measure-Object -Minimum -Maximum -Average
Write-Output ("PASS app lifecycle: cycles={0}; working set MiB min/avg/max={1}/{2}/{3}; close seconds avg/max={4}/{5}; no main app process remains." -f
    $records.Count, $memory.Minimum, [Math]::Round($memory.Average, 1), $memory.Maximum,
    [Math]::Round($close.Average, 2), $close.Maximum)
