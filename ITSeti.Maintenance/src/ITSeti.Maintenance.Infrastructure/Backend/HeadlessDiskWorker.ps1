param([string]$ScriptRoot,[string]$ToolsRoot,[switch]$SkipBenchmark,[switch]$UserMode)
$ErrorActionPreference='Stop'
$BenchmarkReadyFile=Join-Path $ScriptRoot 'benchmark-ready.signal'
$script:Snapshot=@{Smart=@();Notes=@()}
$script:DiskResult=$null
$script:DiskFailure=''
$script:DiskReadLatencyWeighted=0.0
$script:DiskWriteLatencyWeighted=0.0
$script:DiskReadOperations=[long]0
$script:DiskWriteOperations=[long]0
$script:DiskLatencyIssues=@()
$target=Join-Path $ScriptRoot 'diskspd-test.dat'
function Write-DiskProgress([string]$Message) {
    $path=Join-Path $ScriptRoot 'disk-progress.log'
    $stream=[IO.File]::Open($path,[IO.FileMode]::Append,[IO.FileAccess]::Write,[IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
    try {
        $writer=New-Object IO.StreamWriter($stream,(New-Object Text.UTF8Encoding($false)))
        try {$writer.WriteLine($Message);$writer.Flush()} finally {$writer.Dispose()}
    } finally {$stream.Dispose()}
}
function Assert-DiskCheckNotCancelled {
    $request=Join-Path $ScriptRoot 'cancel-request.txt'
    if(!(Test-Path -LiteralPath $request -PathType Leaf)){return}
    $value=[IO.File]::ReadAllText($request).Trim()
    if($value -eq 'cancel'){throw 'Проверка остановлена пользователем.'}
}
function Read-Speed([string]$Path,[string]$Metric) {
    $output=[IO.File]::ReadAllText($Path,[Text.Encoding]::UTF8)
    $end=$output.LastIndexOf('</Results>',[StringComparison]::Ordinal)
    if($end -lt 0){throw 'DiskSpd returned no complete XML result.'}
    [xml]$result=$output.Substring(0,$end+'</Results>'.Length)
    $span=$result.Results.TimeSpan
    if(!$span){throw 'DiskSpd returned no result timespan.'}
    $seconds=[double]::Parse([string]$span.TestTimeSeconds,[Globalization.CultureInfo]::InvariantCulture)
    if($seconds -le 0){throw 'DiskSpd returned invalid test duration.'}
    $bytes=0.0
    foreach($node in @($span.Thread.Target)) {
        $value=$node.$Metric
        if($value){$bytes += [double]::Parse([string]$value,[Globalization.CultureInfo]::InvariantCulture)}
    }
    if($bytes -le 0){throw "DiskSpd returned no $Metric."}
    return [math]::Round($bytes/$seconds/1000000,1)
}
function Get-SystemDrivePerfCounter {
    $drive=[string]$env:SystemDrive
    if($drive -notmatch '^[A-Za-z]:$'){return $null}
    $filter="Name LIKE '% $drive'"
    $pattern='(^|\s)'+[regex]::Escape($drive)+'$'
    $matches=@(Get-WmiObject -Class Win32_PerfRawData_PerfDisk_PhysicalDisk -Filter $filter -ErrorAction Stop |
        Where-Object {$_.Name -match $pattern})
    if($matches.Count -ne 1){return $null}
    return $matches[0]
}
function Get-UnsignedCounterDelta32([long]$Start,[long]$End) {
    if($Start -lt 0 -or $Start -gt 4294967295 -or $End -lt 0 -or $End -gt 4294967295){return $null}
    if($End -ge $Start){return ($End-$Start)}
    return ((4294967296-$Start)+$End)
}
function Measure-DiskLatencyDelta($Before,$After,[ValidateSet('Read','Write')][string]$Mode) {
    if(!$Before -or !$After){return $null}
    $frequencyBefore=[long]$Before.Frequency_Sys100NS
    $frequencyAfter=[long]$After.Frequency_Sys100NS
    if($frequencyBefore -le 0 -or $frequencyBefore -ne $frequencyAfter){return $null}
    $numeratorName='AvgDisksecPer'+$Mode
    $baseName=$numeratorName+'_Base'
    $timeTicks=Get-UnsignedCounterDelta32 ([long]$Before.$numeratorName) ([long]$After.$numeratorName)
    $operations=Get-UnsignedCounterDelta32 ([long]$Before.$baseName) ([long]$After.$baseName)
    if($null -eq $timeTicks -or $null -eq $operations -or $operations -le 0){return $null}
    $latencyMs=($timeTicks/$frequencyBefore)*1000.0/$operations
    if([double]::IsNaN($latencyMs) -or [double]::IsInfinity($latencyMs) -or $latencyMs -lt 0 -or $latencyMs -gt 60000){return $null}
    return [pscustomobject]@{LatencyMs=$latencyMs;Operations=[long]$operations}
}
function Test-PrivilegeWarningOnly([string]$Message) {
    $lines=@($Message -split "`r?`n" | Where-Object {$_.Trim()})
    return $lines.Count -eq 2 -and
        $lines[0] -match '^WARNING: Error adjusting token privileges for SeManageVolumePrivilege \(error code: 1300\)$' -and
        $lines[1] -match '^WARNING: Could not set privileges for setting valid file size; will use a slower method of preparing the file$'
}
function Test-DiskSpdExit($Code,[string]$ErrorText) {
    if($null -eq $Code){return $false}
    if($Code -eq 0){return !$ErrorText.Trim() -or (Test-PrivilegeWarningOnly $ErrorText)}
    return $Code -eq 1 -and (Test-PrivilegeWarningOnly $ErrorText)
}
function Invoke-DiskSpd([string]$Exe,[string]$Mode,[int]$Pass) {
    Write-DiskProgress "DiskSpd: $Mode, pass $Pass/2 - started"
    $out=Join-Path $ScriptRoot ("diskspd-$Mode-$Pass.xml")
    $err=Join-Path $ScriptRoot ("diskspd-$Mode-$Pass.err.txt")
    $write=if($Mode -eq 'write'){'-w100'}else{'-w0'}
    $args="-c1G -b1M -t1 -o1 -s $write -Sh -W1 -d8 -C0 -Rxml `"$target`""
    $counterBefore=$null
    $collectLatency=$Mode -eq 'write' -or $Pass -gt 1
    if($collectLatency) {
        try {$counterBefore=Get-SystemDrivePerfCounter;if(!$counterBefore){$script:DiskLatencyIssues+=@('Счётчик системного диска не найден перед проходом '+$Mode+'.')}}
        catch {$script:DiskLatencyIssues+=@('Счётчик диска недоступен перед проходом '+$Mode+': '+$_.Exception.Message)}
    }
    try {$process=Start-Process -FilePath $Exe -WorkingDirectory (Split-Path $Exe) -ArgumentList $args -WindowStyle Hidden -RedirectStandardOutput $out -RedirectStandardError $err -PassThru -ErrorAction Stop}
    catch {throw "Не удалось запустить DiskSpd ($Exe): $($_.Exception.Message). Проверьте права файла и журнал антивируса."}
    # Retain the native handle before waiting: Windows PowerShell's Start-Process
    # can otherwise lose ExitCode when the child exits and its handle is released.
    $handle=$process.Handle
    $deadline=[DateTime]::UtcNow.AddSeconds(90)
    try {
        while(!$process.WaitForExit(500) -and [DateTime]::UtcNow -lt $deadline){Assert-DiskCheckNotCancelled}
    } catch {
        try {$process.Refresh();if(!$process.HasExited){$process.Kill()}} catch {}
        throw
    }
    if(!$process.HasExited) {
        $process.Refresh()
        if(!$process.HasExited){Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue}
        throw "DiskSpd $Mode pass $Pass timed out."
    }
    $exitCode=$null
    try {$exitCode=$process.ExitCode} catch [InvalidOperationException] {}
    $errorText=[IO.File]::ReadAllText($err)
    if(!(Test-DiskSpdExit $exitCode $errorText)){
        throw "DiskSpd $Mode pass $Pass failed (code $exitCode): $errorText"
    }
    $speed=Read-Speed $out $(if($Mode -eq 'write'){'WriteBytes'}else{'ReadBytes'})
    if($collectLatency -and $counterBefore) {
        try {
            $counterAfter=Get-SystemDrivePerfCounter
            $measurement=Measure-DiskLatencyDelta $counterBefore $counterAfter $Mode
            if($measurement) {
                if($Mode -eq 'read') {$script:DiskReadLatencyWeighted+=$measurement.LatencyMs*$measurement.Operations;$script:DiskReadOperations+=$measurement.Operations}
                else {$script:DiskWriteLatencyWeighted+=$measurement.LatencyMs*$measurement.Operations;$script:DiskWriteOperations+=$measurement.Operations}
            } else {$script:DiskLatencyIssues+=@('Системный счётчик не получил операций для расчёта задержки '+$Mode+'.')}
        } catch {$script:DiskLatencyIssues+=@('Счётчик диска недоступен после прохода '+$Mode+': '+$_.Exception.Message)}
    }
    Write-DiskProgress "DiskSpd: $Mode, pass $Pass/2 - $speed MB/s"
    return $speed
}
try {
    Start-Transcript -Path (Join-Path $ScriptRoot 'disk-worker.log') -Force | Out-Null
    Assert-DiskCheckNotCancelled
    . ([scriptblock]::Create([IO.File]::ReadAllText((Join-Path $ScriptRoot 'Summary.ps1'),[Text.Encoding]::UTF8)))
    if($UserMode) {
        # CrystalDiskInfo requests elevation on some builds. User checks must not
        # turn that into a credentials/UAC prompt; SMART remains an engineer detail.
        $script:Snapshot.Notes+='SMART: пропущено в пользовательской проверке без запроса прав администратора.'
        Write-DiskProgress 'CrystalDiskInfo: пропущено в пользовательской проверке'
    } else {
        Write-DiskProgress 'CrystalDiskInfo: SMART export started'
        $info=Join-Path $ToolsRoot 'CrystalDiskInfo9_6_3_Portable\DiskInfo64.exe'
        try {
        if(!(Test-Path -LiteralPath $info -PathType Leaf)){throw 'CrystalDiskInfo is missing.'}
        $report=Join-Path (Split-Path $info) 'DiskInfo.txt'
        $started=Get-Date
        try {$export=Start-Process -FilePath $info -WorkingDirectory (Split-Path $info) -ArgumentList '/CopyExit' -WindowStyle Hidden -PassThru -ErrorAction Stop}
        catch {throw "Не удалось запустить CrystalDiskInfo ($info): $($_.Exception.Message). Проверьте права файла и журнал антивируса."}
        $exportDeadline=[DateTime]::UtcNow.AddSeconds(30)
        try {
            while(!$export.WaitForExit(500) -and [DateTime]::UtcNow -lt $exportDeadline){Assert-DiskCheckNotCancelled}
        } catch {
            try {$export.Refresh();if(!$export.HasExited){$export.Kill()}} catch {}
            throw
        }
        if(!$export.HasExited){try {$export.Kill()} catch {};throw 'SMART export timed out.'}
        if(!(Test-Path -LiteralPath $report) -or (Get-Item -LiteralPath $report).LastWriteTime -lt $started.AddSeconds(-2)){throw 'Fresh SMART report not found.'}
        $script:Snapshot.Smart=@(ConvertFrom-CdiReport ([IO.File]::ReadAllText($report)))
        if(!$script:Snapshot.Smart.Count){throw 'SMART report could not be parsed.'}
        Write-DiskProgress "CrystalDiskInfo: SMART data received for $($script:Snapshot.Smart.Count) drive(s)"
        } catch {$script:Snapshot.Notes+=('SMART: '+$_.Exception.Message);Write-DiskProgress ('CrystalDiskInfo: '+$_.Exception.Message)}
    }
    if($SkipBenchmark){Write-DiskProgress 'DiskSpd: пропущено быстрой проверкой';return}
    if($BenchmarkReadyFile) {
        Write-DiskProgress 'DiskSpd: ожидание окончания выборки нагрузки'
        $readyDeadline=[DateTime]::UtcNow.AddSeconds(120)
        while(!(Test-Path -LiteralPath $BenchmarkReadyFile -PathType Leaf) -and [DateTime]::UtcNow -lt $readyDeadline){Assert-DiskCheckNotCancelled;Start-Sleep -Milliseconds 250}
        if(!(Test-Path -LiteralPath $BenchmarkReadyFile -PathType Leaf)){throw 'Resource sampling did not signal benchmark readiness.'}
    }
    $exe=Join-Path $ToolsRoot 'CrystalDiskMark9\CdmResource\DiskSpd\DiskSpd64.exe'
    if(!(Test-Path -LiteralPath $exe -PathType Leaf)){throw 'DiskSpd64.exe is missing from the installed CrystalDiskMark bundle.'}
    $drive=Get-WmiObject Win32_LogicalDisk -Filter "DeviceID='$env:SystemDrive'" -ErrorAction Stop
    if(!$drive -or $drive.DriveType -ne 3 -or $drive.FreeSpace -lt 3GB){throw 'System disk needs at least 3 GiB free for benchmark.'}
    $read=@();$write=@()
    for($pass=1;$pass -le 2;$pass++) {
        Assert-DiskCheckNotCancelled
        $read+=Invoke-DiskSpd $exe 'read' $pass
        Assert-DiskCheckNotCancelled
        $write+=Invoke-DiskSpd $exe 'write' $pass
    }
    $media=Get-SystemDiskMediaType
    if($media -eq 'Unknown') {
        $match=@($script:Snapshot.Smart | Where-Object {($_.Letters -split '[, ]+') -contains $env:SystemDrive})
        if($match.Count -eq 1){$media=$match[0].MediaType}
    }
    $systemSmart=@($script:Snapshot.Smart | Where-Object {($_.Letters -split '[, ]+') -contains $env:SystemDrive})
    $isNvme=$media -eq 'SSD' -and (($systemSmart | Where-Object {$_.TransferMode -match 'PCIe|NVMe|NVM Express'}).Count -gt 0)
    $readLatency=if($script:DiskReadOperations -gt 0){[math]::Round($script:DiskReadLatencyWeighted/$script:DiskReadOperations,3)}else{$null}
    $writeLatency=if($script:DiskWriteOperations -gt 0){[math]::Round($script:DiskWriteLatencyWeighted/$script:DiskWriteOperations,3)}else{$null}
    $script:DiskResult=@{Read=[math]::Round(($read | Measure-Object -Average).Average,1);Write=[math]::Round(($write | Measure-Object -Average).Average,1);MediaType=$media;IsNvme=$isNvme;Passes=2;Drive=$env:SystemDrive;DiskReadLatencyMs=$readLatency;DiskWriteLatencyMs=$writeLatency;DiskReadOperations=$script:DiskReadOperations;DiskWriteOperations=$script:DiskWriteOperations}
    Write-DiskProgress "DiskSpd: finished - read $($script:DiskResult.Read) MB/s, write $($script:DiskResult.Write) MB/s"
} catch {$script:DiskFailure=$_.Exception.Message;Write-DiskProgress ('DiskSpd: '+$_.Exception.Message)}
finally {
    if(Test-Path -LiteralPath $target -PathType Leaf){Remove-Item -LiteralPath $target -Force -ErrorAction SilentlyContinue}
    $latencyError=($script:DiskLatencyIssues -join '; ')
    $result=@{Result=$script:DiskResult;Failure=$script:DiskFailure;LatencyError=$latencyError;Smart=$script:Snapshot.Smart;Notes=$script:Snapshot.Notes;Owned=@()}
    $pending=Join-Path $ScriptRoot 'disk-result.pending.xml'
    $result | Export-Clixml -LiteralPath $pending
    Move-Item -LiteralPath $pending -Destination (Join-Path $ScriptRoot 'disk-result.xml') -Force
    Stop-Transcript | Out-Null
}
