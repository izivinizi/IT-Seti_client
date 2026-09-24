param([string]$ScriptRoot,[string]$ToolsRoot,[ValidateSet(1,2)][int]$Passes=1,[switch]$Quiet)
$ErrorActionPreference='Stop'
$script:Admin=([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$script:CompactOutput=$true
$script:DiskTestPasses=$Passes
$script:QuietDiskTools=[bool]$Quiet
$script:Snapshot=@{Smart=@();Notes=@()}
function Section([string]$Text){Write-Output $Text}
$logging=$false
try {
    Start-Transcript -Path (Join-Path $ScriptRoot 'disk-worker.log') -Force | Out-Null
    $logging=$true
    foreach($file in @('Summary.ps1','DiskTools.ps1')){. ([scriptblock]::Create([IO.File]::ReadAllText((Join-Path $ScriptRoot $file),[Text.Encoding]::UTF8)))}
    Start-DiskTools
} catch {$script:DiskFailure=$_.Exception.Message}
finally {
    $owned=@(foreach($item in $script:OwnedDiskTools){@{Id=$item.Process.Id;Path=$item.Path;Started=$item.Started;Benchmark=$item.Benchmark}})
    $result=@{Result=$script:DiskResult;Failure=$script:DiskFailure;Smart=$script:Snapshot.Smart;Notes=$script:Snapshot.Notes;Owned=$owned}
    $temporary=Join-Path $ScriptRoot 'disk-result.pending.xml'
    $result | Export-Clixml -LiteralPath $temporary
    Move-Item -LiteralPath $temporary -Destination (Join-Path $ScriptRoot 'disk-result.xml') -Force
    if($Quiet -and (Get-Command Close-OwnedDiskTools -ErrorAction SilentlyContinue)){Close-OwnedDiskTools}
    if($logging){Stop-Transcript | Out-Null}
}
