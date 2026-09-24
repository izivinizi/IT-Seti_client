param([string]$Root)
$ErrorActionPreference='Stop'
$backend=Join-Path $Root 'src\ITSeti.Maintenance.Infrastructure\Backend'
. ([scriptblock]::Create([IO.File]::ReadAllText((Join-Path $backend 'DiskTools.ps1'),[Text.Encoding]::UTF8)))
$ScriptRoot=$backend;$ToolsRoot=$backend;$script:Admin=$true
$script:DiskTestPasses=2;$script:QuietDiskTools=$true
function Start-Process {
    param($FilePath,$WindowStyle,$WorkingDirectory,$ArgumentList,[switch]$PassThru,$ErrorAction)
    if($WindowStyle -ne 'Hidden'){throw 'Worker must not open a console'}
    $payload=($ArgumentList -split 'EncodedCommand ')[1]
    $script:WorkerCommand=[Text.Encoding]::Unicode.GetString([Convert]::FromBase64String($payload))
    return New-Object PSObject -Property @{Id=42}
}
Start-DiskToolsBackground
if($script:WorkerCommand -notmatch '-Passes 2 -Quiet$'){throw 'Two-pass, quiet worker options missing'}
$worker=[IO.File]::ReadAllText((Join-Path $backend 'DiskWorker.ps1'),[Text.Encoding]::UTF8)
$tools=[IO.File]::ReadAllText((Join-Path $backend 'DiskTools.ps1'),[Text.Encoding]::UTF8)
if($worker -notmatch '\$script:DiskTestPasses=\$Passes' -or $tools -notmatch 'if\(\$script:DiskTestPasses -eq 2\)\{\$passes=2\}' -or $tools -notmatch 'Id=1026;Label='){throw 'Two-pass selector not connected'}
'PASS: quiet, two-pass CDM worker command and selector connected; no disk test launched.'
