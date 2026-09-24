param([string]$Root,[switch]$LimitedMode,[switch]$WithResourceSampling,[ValidateSet('Debug','Release')][string]$Configuration='Debug')
$ErrorActionPreference='Stop'
$bundle=Join-Path $Root "src\ITSeti.Maintenance.App\bin\$Configuration\net9.0-windows\Backend"
$run=Join-Path $Root ('artifacts\backend-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $run -Force | Out-Null
Get-ChildItem -LiteralPath $bundle -File | Copy-Item -Destination $run
& ([scriptblock]::Create([IO.File]::ReadAllText((Join-Path $run 'FullCheckWorker.ps1'),[Text.Encoding]::UTF8))) -RunRoot $run -ToolsRoot '' -SkipDiskTests -HeadlessDiskSpd:$WithResourceSampling -SkipResourceSampling:(!$WithResourceSampling) -LimitedMode:$LimitedMode
if(!(Test-Path -LiteralPath (Join-Path $run 'result.json'))){throw ([IO.File]::ReadAllText((Join-Path $run 'error.txt')))}
$data=Get-Content -LiteralPath (Join-Path $run 'result.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if($data.Full.Benchmark.State -ne 'Skipped' -or !$data.Full.CpuName -or !$data.Full.GpuName -or !@($data.Disks).Count -or !$data.LastBootAt){throw 'Incomplete full diagnostic payload'}
if(!$data.Full.Processes -and !$data.Full.ProcessUnavailable){throw 'No process coverage'}
if(!$data.Full.Events -and !$data.Full.EventUnavailable){throw 'No event coverage'}
if(@($data.Full.TopMemoryProcesses).Count -lt 1 -or !$data.Full.TopMemoryProcesses[0].DisplayName){throw 'No friendly top-memory application names'}
if($WithResourceSampling -and ($data.Full.ResourceSampling.Samples -lt 6 -or $data.Full.ResourceSampling.Error)){throw '30-second resource sample missing or failed'}
if($LimitedMode -and ($data.Full.Benchmark.Error -notmatch 'SMART' -or @($data.Notes | Where-Object {$_ -match 'SMART'}).Count -ne 1)){throw 'Limited mode not marked in report'}
"PASS: real hardware/process/event backend; disk write/cleanup/repair skipped. $run"
