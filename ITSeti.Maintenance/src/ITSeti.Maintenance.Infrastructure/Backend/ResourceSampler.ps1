param([string]$RunRoot,[int]$Seconds=30,[int]$Interval=5)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'HWiNFO.ps1')
try {
    $total=[double](Get-WmiObject Win32_ComputerSystem -ErrorAction Stop).TotalPhysicalMemory
    $sampleCount=[int][math]::Floor($Seconds/$Interval)+1
    $cpuValues=@();$memoryValues=@();$pageValues=@()
    $cpuHigh=0;$cpuCritical=0;$memoryHigh=0;$memoryCritical=0;$lowAvailable=0;$pagingHigh=0
    $sampleClock=[Diagnostics.Stopwatch]::StartNew()
    for($index=0;$index -lt $sampleCount;$index++) {
        if($index -gt 0) {
            $waitMilliseconds=($index*$Interval*1000)-[int]$sampleClock.ElapsedMilliseconds
            if($waitMilliseconds -gt 0){Start-Sleep -Milliseconds $waitMilliseconds}
        }
        $memory=Get-WmiObject Win32_PerfFormattedData_PerfOS_Memory -ErrorAction Stop
        $cpuPercent=Get-HwInfoCpuLoadPercent
        if($null -eq $cpuPercent){throw 'Свежая загрузка CPU из HWiNFO недоступна.'}
        $availableMb=[double]$memory.AvailableMBytes
        $usedPercent=if($total -gt 0){[math]::Min(100,[math]::Max(0,100*(1-$availableMb*1MB/$total)))}else{0}
        $cpuPercent=[math]::Min(100,[math]::Max(0,$cpuPercent))
        $pagesOut=[double]$memory.PagesOutputPerSec
        $cpuValues+=@($cpuPercent);$memoryValues+=@($usedPercent);$pageValues+=@($pagesOut)
        if($cpuPercent -ge 90){$cpuHigh++}
        if($cpuPercent -ge 98){$cpuCritical++}
        if($usedPercent -ge 90){$memoryHigh++}
        if($usedPercent -ge 98){$memoryCritical++}
        if($availableMb -lt 500){$lowAvailable++}
        if($pagesOut -ge 100){$pagingHigh++}
        $progressResult=@{
            Samples=$index+1;CpuHighSamples=$cpuHigh;MemoryHighSamples=$memoryHigh
            CpuCriticalSamples=$cpuCritical;MemoryCriticalSamples=$memoryCritical
            LowAvailableSamples=$lowAvailable;PagingHighSamples=$pagingHigh
            CpuAverage=[math]::Round(($cpuValues | Measure-Object -Average).Average,1)
            MemoryAverage=[math]::Round(($memoryValues | Measure-Object -Average).Average,1)
            PagesOutputAverage=[math]::Round(($pageValues | Measure-Object -Average).Average,1)
            DiskReadLatencyMs=$null;DiskWriteLatencyMs=$null
            DiskReadOperations=0;DiskWriteOperations=0;Error=''
        }
        $pending=Join-Path $RunRoot 'resource-sample-progress.pending.json'
        $progressPath=Join-Path $RunRoot 'resource-sample-progress.json'
        $progressResult | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $pending -Encoding UTF8
        Move-Item -LiteralPath $pending -Destination $progressPath -Force
    }
    $result=@{
        Samples=$sampleCount;CpuHighSamples=$cpuHigh;MemoryHighSamples=$memoryHigh
        CpuCriticalSamples=$cpuCritical;MemoryCriticalSamples=$memoryCritical
        LowAvailableSamples=$lowAvailable;PagingHighSamples=$pagingHigh
        CpuAverage=[math]::Round(($cpuValues | Measure-Object -Average).Average,1)
        MemoryAverage=[math]::Round(($memoryValues | Measure-Object -Average).Average,1)
        PagesOutputAverage=[math]::Round(($pageValues | Measure-Object -Average).Average,1)
        DiskReadLatencyMs=$null;DiskWriteLatencyMs=$null
        DiskReadOperations=0;DiskWriteOperations=0;Error=''
    }
    $result | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $RunRoot 'resource-sample.json') -Encoding UTF8
} catch {
    @{Samples=0;CpuHighSamples=0;MemoryHighSamples=0;CpuCriticalSamples=0;MemoryCriticalSamples=0;LowAvailableSamples=0;PagingHighSamples=0
        CpuAverage=0;MemoryAverage=0;PagesOutputAverage=0;DiskReadLatencyMs=$null;DiskWriteLatencyMs=$null
        DiskReadOperations=0;DiskWriteOperations=0;Error=$_.Exception.Message} |
        ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $RunRoot 'resource-sample.json') -Encoding UTF8
}
