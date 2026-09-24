param([switch]$StartRepair,[switch]$Quick)
$ErrorActionPreference='Stop'
$base=Join-Path $env:ProgramData 'ITSeti\Maintenance'
$runs=Join-Path $base 'Runs'
$tools=Join-Path (Split-Path $PSScriptRoot -Parent) 'Tools'
$run=Join-Path $runs ([guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $run -Force | Out-Null
$source=$PSScriptRoot
foreach($file in @('FullCheckWorker.ps1','HeadlessDiskWorker.ps1','ResourceSampler.ps1','Summary.ps1','DiskTools.ps1','Runtime.ps1','EventWorker.ps1','Repair.ps1','RepairWorker.ps1','NativeDiskMark.cs','allowed-processes.json','allowed-processes-win7.txt','allowed-publishers.txt','Set-WindowsAutomaticUpdates.ps1','Update-Application.ps1')) {
    Copy-Item -LiteralPath (Join-Path $source $file) -Destination $run -ErrorAction Stop
}
$pending=Join-Path $base 'latest.pending.txt'
[IO.File]::WriteAllText($pending,$run,[Text.Encoding]::UTF8)
Move-Item -LiteralPath $pending -Destination (Join-Path $base 'latest.txt') -Force
& ([scriptblock]::Create([IO.File]::ReadAllText((Join-Path $run 'FullCheckWorker.ps1'),[Text.Encoding]::UTF8))) -RunRoot $run -ToolsRoot $tools -HeadlessDiskSpd -TrustedTools -StartRepair:$StartRepair -SkipResourceSampling:$Quick -SkipDiskBenchmark:$Quick
