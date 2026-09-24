param([string]$ScriptRoot,[string]$ToolsRoot,[switch]$SkipBenchmark)
$ErrorActionPreference='Stop'
$script:Snapshot=@{Smart=@();Notes=@()}
$script:DiskResult=$null
$script:DiskFailure=''
$target=Join-Path $ScriptRoot 'diskspd-test.dat'
function Write-DiskProgress([string]$Message) {
    [IO.File]::AppendAllText((Join-Path $ScriptRoot 'disk-progress.log'),($Message+[Environment]::NewLine),(New-Object Text.UTF8Encoding($false)))
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
    try {$process=Start-Process -FilePath $Exe -WorkingDirectory (Split-Path $Exe) -ArgumentList $args -WindowStyle Hidden -RedirectStandardOutput $out -RedirectStandardError $err -PassThru -ErrorAction Stop}
    catch {throw "Не удалось запустить DiskSpd ($Exe): $($_.Exception.Message). Проверьте права файла и журнал антивируса."}
    # Retain the native handle before waiting: Windows PowerShell's Start-Process
    # can otherwise lose ExitCode when the child exits and its handle is released.
    $handle=$process.Handle
    if(!$process.WaitForExit(90000)) {
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
    Write-DiskProgress "DiskSpd: $Mode, pass $Pass/2 - $speed MB/s"
    return $speed
}
try {
    Start-Transcript -Path (Join-Path $ScriptRoot 'disk-worker.log') -Force | Out-Null
    . ([scriptblock]::Create([IO.File]::ReadAllText((Join-Path $ScriptRoot 'Summary.ps1'),[Text.Encoding]::UTF8)))
    Write-DiskProgress 'CrystalDiskInfo: SMART export started'
    $info=Join-Path $ToolsRoot 'CrystalDiskInfo9_6_3_Portable\DiskInfo64.exe'
    try {
        if(!(Test-Path -LiteralPath $info -PathType Leaf)){throw 'CrystalDiskInfo is missing.'}
        $report=Join-Path (Split-Path $info) 'DiskInfo.txt'
        $started=Get-Date
        try {$export=Start-Process -FilePath $info -WorkingDirectory (Split-Path $info) -ArgumentList '/CopyExit' -WindowStyle Hidden -PassThru -ErrorAction Stop}
        catch {throw "Не удалось запустить CrystalDiskInfo ($info): $($_.Exception.Message). Проверьте права файла и журнал антивируса."}
        if(!$export.WaitForExit(30000)){throw 'SMART export timed out.'}
        if(!(Test-Path -LiteralPath $report) -or (Get-Item -LiteralPath $report).LastWriteTime -lt $started.AddSeconds(-2)){throw 'Fresh SMART report not found.'}
        $script:Snapshot.Smart=@(ConvertFrom-CdiReport ([IO.File]::ReadAllText($report)))
        if(!$script:Snapshot.Smart.Count){throw 'SMART report could not be parsed.'}
        Write-DiskProgress "CrystalDiskInfo: SMART data received for $($script:Snapshot.Smart.Count) drive(s)"
    } catch {$script:Snapshot.Notes+=('SMART: '+$_.Exception.Message);Write-DiskProgress ('CrystalDiskInfo: '+$_.Exception.Message)}
    if($SkipBenchmark){Write-DiskProgress 'DiskSpd: пропущено быстрой проверкой';return}
    $exe=Join-Path $ToolsRoot 'CrystalDiskMark9\CdmResource\DiskSpd\DiskSpd64.exe'
    if(!(Test-Path -LiteralPath $exe -PathType Leaf)){throw 'DiskSpd64.exe is missing from the installed CrystalDiskMark bundle.'}
    $drive=Get-WmiObject Win32_LogicalDisk -Filter "DeviceID='$env:SystemDrive'" -ErrorAction Stop
    if(!$drive -or $drive.DriveType -ne 3 -or $drive.FreeSpace -lt 3GB){throw 'System disk needs at least 3 GiB free for benchmark.'}
    $read=@();$write=@()
    for($pass=1;$pass -le 2;$pass++) {
        $read+=Invoke-DiskSpd $exe 'read' $pass
        $write+=Invoke-DiskSpd $exe 'write' $pass
    }
    $media=Get-SystemDiskMediaType
    if($media -eq 'Unknown') {
        $match=@($script:Snapshot.Smart | Where-Object {($_.Letters -split '[, ]+') -contains $env:SystemDrive})
        if($match.Count -eq 1){$media=$match[0].MediaType}
    }
    $systemSmart=@($script:Snapshot.Smart | Where-Object {($_.Letters -split '[, ]+') -contains $env:SystemDrive})
    $isNvme=$media -eq 'SSD' -and (($systemSmart | Where-Object {$_.TransferMode -match 'PCIe|NVMe|NVM Express'}).Count -gt 0)
    $script:DiskResult=@{Read=[math]::Round(($read | Measure-Object -Average).Average,1);Write=[math]::Round(($write | Measure-Object -Average).Average,1);MediaType=$media;IsNvme=$isNvme;Passes=2;Drive=$env:SystemDrive}
    Write-DiskProgress "DiskSpd: finished - read $($script:DiskResult.Read) MB/s, write $($script:DiskResult.Write) MB/s"
} catch {$script:DiskFailure=$_.Exception.Message;Write-DiskProgress ('DiskSpd: '+$_.Exception.Message)}
finally {
    if(Test-Path -LiteralPath $target -PathType Leaf){Remove-Item -LiteralPath $target -Force -ErrorAction SilentlyContinue}
    $result=@{Result=$script:DiskResult;Failure=$script:DiskFailure;Smart=$script:Snapshot.Smart;Notes=$script:Snapshot.Notes;Owned=@()}
    $pending=Join-Path $ScriptRoot 'disk-result.pending.xml'
    $result | Export-Clixml -LiteralPath $pending
    Move-Item -LiteralPath $pending -Destination (Join-Path $ScriptRoot 'disk-result.xml') -Force
    Stop-Transcript | Out-Null
}
