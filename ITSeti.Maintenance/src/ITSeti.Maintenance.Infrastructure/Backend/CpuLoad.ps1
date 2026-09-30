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
}
'@
}
function Get-CpuTimeSample {
    $idle=New-Object ITSetiCpuTimes+FileTime
    $kernel=New-Object ITSetiCpuTimes+FileTime
    $user=New-Object ITSetiCpuTimes+FileTime
    if(![ITSetiCpuTimes]::GetSystemTimes([ref]$idle,[ref]$kernel,[ref]$user)){throw 'Windows CPU counters unavailable.'}
    return [pscustomobject]@{Idle=$idle.Value;Kernel=$kernel.Value;User=$user.Value}
}
function Get-CpuLoadPercent([int]$Milliseconds=200) {
    try {
        $first=Get-CpuTimeSample
        Start-Sleep -Milliseconds $Milliseconds
        $second=Get-CpuTimeSample
        $total=([double]$second.Kernel-$first.Kernel)+([double]$second.User-$first.User)
        $idle=[double]$second.Idle-$first.Idle
        if($total -le 0 -or $idle -lt 0){throw 'Windows CPU counters did not advance.'}
        return [math]::Min(100,[math]::Max(0,100*($total-$idle)/$total))
    } catch {
        $counter=Get-WmiObject Win32_PerfFormattedData_PerfOS_Processor -Filter "Name='_Total'" -ErrorAction Stop
        return [double]$counter.PercentProcessorTime
    }
}
