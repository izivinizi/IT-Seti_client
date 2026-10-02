if(-not ('ITSetiCpuTimes' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class ITSetiCpuTimes {
    [StructLayout(LayoutKind.Sequential)] public struct FileTime {
        public uint Low, High;
        public ulong Value { get { return ((ulong)High << 32) | Low; } }
    }
    [DllImport("kernel32.dll", SetLastError=true)]
    public static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);
    [DllImport("kernel32.dll")]
    public static extern bool QueryUnbiasedInterruptTime(out ulong time);
}
'@
}
function Get-ActiveUptimeHours {
    [uint64]$ticks=0
    if([ITSetiCpuTimes]::QueryUnbiasedInterruptTime([ref]$ticks)){
        return [math]::Round($ticks / 36000000000.0, 2)
    }
    return $null
}
function Get-CpuTimeSample {
    $idle=New-Object ITSetiCpuTimes+FileTime
    $kernel=New-Object ITSetiCpuTimes+FileTime
    $user=New-Object ITSetiCpuTimes+FileTime
    if(![ITSetiCpuTimes]::GetSystemTimes([ref]$idle,[ref]$kernel,[ref]$user)){throw 'Windows CPU counters unavailable.'}
    return [pscustomobject]@{Idle=$idle.Value;Kernel=$kernel.Value;User=$user.Value}
}
function Get-ProcessCpuPercent {
    $first=@{}
    foreach($process in @(Get-Process -ErrorAction SilentlyContinue)){
        if($null -ne $process.CPU){$first[$process.Id]=[double]$process.CPU}
    }
    $watch=[Diagnostics.Stopwatch]::StartNew()
    Start-Sleep -Milliseconds 750
    $seconds=0.0
    foreach($process in @(Get-Process -ErrorAction SilentlyContinue)){
        if($first.ContainsKey($process.Id) -and $null -ne $process.CPU){
            $seconds += [math]::Max(0,([double]$process.CPU-$first[$process.Id]))
        }
    }
    $watch.Stop()
    if($first.Count -eq 0 -or $watch.Elapsed.TotalSeconds -le 0){return $null}
    return [math]::Min(100,[math]::Max(0,100*$seconds/$watch.Elapsed.TotalSeconds/[Environment]::ProcessorCount))
}
function Get-CpuLoadPercent([int]$Milliseconds=500) {
    try {
        $first=Get-CpuTimeSample
        Start-Sleep -Milliseconds $Milliseconds
        $second=Get-CpuTimeSample
        $total=([double]$second.Kernel-$first.Kernel)+([double]$second.User-$first.User)
        $idle=[double]$second.Idle-$first.Idle
        if($total -le 0 -or $idle -lt 0){throw 'Windows CPU counters did not advance.'}
        $native=[math]::Min(100,[math]::Max(0,100*($total-$idle)/$total))
        if($native -gt 0.5 -and $native -lt 99.5){return $native}
        try {
            $alternate=[double](Get-WmiObject Win32_PerfFormattedData_PerfOS_Processor -Filter "Name='_Total'" -ErrorAction Stop).PercentProcessorTime
            if($alternate -gt 0.5 -and $alternate -lt 99.5){return $alternate}
        } catch {}
        try {
            $processLoad=Get-ProcessCpuPercent
            if($null -ne $processLoad -and $processLoad -gt 0.5){return $processLoad}
        } catch {}
        return $native
    } catch {
        try {
            $counter=Get-WmiObject Win32_PerfFormattedData_PerfOS_Processor -Filter "Name='_Total'" -ErrorAction Stop
            if($counter -and [double]$counter.PercentProcessorTime -gt 0.5){return [double]$counter.PercentProcessorTime}
        } catch {}
        return Get-ProcessCpuPercent
    }
}
