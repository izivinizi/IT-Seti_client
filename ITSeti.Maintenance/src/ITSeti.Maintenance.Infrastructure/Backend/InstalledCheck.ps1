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
$cpuTemperatureFile=Join-Path $run 'cpu-temperature.json'
$probe=Join-Path (Split-Path $PSScriptRoot -Parent) 'ITSeti.Maintenance.exe'
try {
    $probeProcess=Start-Process -FilePath $probe -ArgumentList @('--cpu-temperature-probe',('"'+$cpuTemperatureFile+'"')) -PassThru -WindowStyle Hidden -ErrorAction Stop
    if(!$probeProcess.WaitForExit(30000)){
        $probeProcess.Kill()
        throw 'CPU sensor probe did not finish within 30 seconds.'
    }
    if(!(Test-Path -LiteralPath $cpuTemperatureFile)) {
        @{TemperatureC=$null;Status="Sensor process ended without a result (exit code $($probeProcess.ExitCode))"} | ConvertTo-Json | Set-Content -LiteralPath $cpuTemperatureFile -Encoding UTF8
    }
} catch {
    @{TemperatureC=$null;Status=('Не удалось запустить чтение датчика CPU: '+$_.Exception.Message)} | ConvertTo-Json | Set-Content -LiteralPath $cpuTemperatureFile -Encoding UTF8
}
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
