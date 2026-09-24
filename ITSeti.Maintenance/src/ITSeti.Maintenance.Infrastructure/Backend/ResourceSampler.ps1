param([string]$RunRoot,[int]$Seconds=30,[int]$Interval=5)
$ErrorActionPreference='Stop'
function CounterDelta([double]$Current,[double]$Previous) {
    $difference=$Current-$Previous
    if($difference -lt 0){return $null}
    return $difference
}
try {
    $total=[double](Get-WmiObject Win32_ComputerSystem -ErrorAction Stop).TotalPhysicalMemory
    $sampleCount=[int][math]::Floor($Seconds/$Interval)+1
    $cpuValues=@();$memoryValues=@();$pageValues=@()
    $cpuHigh=0;$memoryHigh=0;$lowAvailable=0;$pagingHigh=0
    $readTicks=0.0;$writeTicks=0.0;$readOps=0.0;$writeOps=0.0
    $previousDisk=$null;$diskFrequency=0.0
    for($index=0;$index -lt $sampleCount;$index++) {
        $cpu=Get-WmiObject Win32_PerfFormattedData_PerfOS_Processor -Filter "Name='_Total'" -ErrorAction Stop
        $memory=Get-WmiObject Win32_PerfFormattedData_PerfOS_Memory -ErrorAction Stop
        $disk=Get-WmiObject Win32_PerfRawData_PerfDisk_LogicalDisk -Filter "Name='$env:SystemDrive'" -ErrorAction SilentlyContinue
        $cpuPercent=[double]$cpu.PercentProcessorTime
        $availableMb=[double]$memory.AvailableMBytes
        $usedPercent=if($total -gt 0){[math]::Min(100,[math]::Max(0,100*(1-$availableMb*1MB/$total)))}else{0}
        $cpuPercent=[math]::Min(100,[math]::Max(0,$cpuPercent))
        $pagesOut=[double]$memory.PagesOutputPerSec
        $cpuValues+=@($cpuPercent);$memoryValues+=@($usedPercent);$pageValues+=@($pagesOut)
        if($cpuPercent -ge 90){$cpuHigh++}
        if($usedPercent -ge 90){$memoryHigh++}
        if($availableMb -lt 500){$lowAvailable++}
        if($pagesOut -ge 100){$pagingHigh++}
        if($disk -and $previousDisk -and !(Get-Process DiskSpd* -ErrorAction SilentlyContinue)) {
            $frequency=[double]$disk.Frequency_PerfTime
            if($frequency -gt 0) {
                $diskFrequency=$frequency
                $reads=[double]$disk.AvgDisksecPerRead_Base-[double]$previousDisk.AvgDisksecPerRead_Base
                $writes=[double]$disk.AvgDisksecPerWrite_Base-[double]$previousDisk.AvgDisksecPerWrite_Base
                if($reads -gt 0){
                    $delta=CounterDelta ([double]$disk.AvgDisksecPerRead) ([double]$previousDisk.AvgDisksecPerRead)
                    if($null -ne $delta){$readOps+=$reads;$readTicks+=$delta}
                }
                if($writes -gt 0){
                    $delta=CounterDelta ([double]$disk.AvgDisksecPerWrite) ([double]$previousDisk.AvgDisksecPerWrite)
                    if($null -ne $delta){$writeOps+=$writes;$writeTicks+=$delta}
                }
            }
        }
        $previousDisk=$disk
        $progressResult=@{
            Samples=$index+1;CpuHighSamples=$cpuHigh;MemoryHighSamples=$memoryHigh
            LowAvailableSamples=$lowAvailable;PagingHighSamples=$pagingHigh
            CpuAverage=[math]::Round(($cpuValues | Measure-Object -Average).Average,1)
            MemoryAverage=[math]::Round(($memoryValues | Measure-Object -Average).Average,1)
            PagesOutputAverage=[math]::Round(($pageValues | Measure-Object -Average).Average,1)
            DiskReadLatencyMs=$(if($readOps -ge 30 -and $diskFrequency -gt 0){[math]::Round(1000*$readTicks/$readOps/$diskFrequency,1)}else{$null})
            DiskWriteLatencyMs=$(if($writeOps -ge 30 -and $diskFrequency -gt 0){[math]::Round(1000*$writeTicks/$writeOps/$diskFrequency,1)}else{$null})
            DiskReadOperations=[long]$readOps;DiskWriteOperations=[long]$writeOps;Error=''
        }
        $pending=Join-Path $RunRoot 'resource-sample-progress.pending.json'
        $progressPath=Join-Path $RunRoot 'resource-sample-progress.json'
        $progressResult | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $pending -Encoding UTF8
        Move-Item -LiteralPath $pending -Destination $progressPath -Force
        if($index -lt $sampleCount-1){Start-Sleep -Seconds $Interval}
    }
    $result=@{
        Samples=$sampleCount;CpuHighSamples=$cpuHigh;MemoryHighSamples=$memoryHigh
        LowAvailableSamples=$lowAvailable;PagingHighSamples=$pagingHigh
        CpuAverage=[math]::Round(($cpuValues | Measure-Object -Average).Average,1)
        MemoryAverage=[math]::Round(($memoryValues | Measure-Object -Average).Average,1)
        PagesOutputAverage=[math]::Round(($pageValues | Measure-Object -Average).Average,1)
        DiskReadLatencyMs=$(if($readOps -ge 30 -and $diskFrequency -gt 0){[math]::Round(1000*$readTicks/$readOps/$diskFrequency,1)}else{$null})
        DiskWriteLatencyMs=$(if($writeOps -ge 30 -and $diskFrequency -gt 0){[math]::Round(1000*$writeTicks/$writeOps/$diskFrequency,1)}else{$null})
        DiskReadOperations=[long]$readOps;DiskWriteOperations=[long]$writeOps;Error=''
    }
    $result | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $RunRoot 'resource-sample.json') -Encoding UTF8
} catch {
    @{Samples=0;CpuHighSamples=0;MemoryHighSamples=0;LowAvailableSamples=0;PagingHighSamples=0
        CpuAverage=0;MemoryAverage=0;PagesOutputAverage=0;DiskReadLatencyMs=$null;DiskWriteLatencyMs=$null
        DiskReadOperations=0;DiskWriteOperations=0;Error=$_.Exception.Message} |
        ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $RunRoot 'resource-sample.json') -Encoding UTF8
}
