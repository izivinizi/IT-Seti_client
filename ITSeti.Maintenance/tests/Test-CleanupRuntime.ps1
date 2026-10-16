$ErrorActionPreference = 'Stop'
$source = Join-Path (Split-Path $PSScriptRoot -Parent) 'src\ITSeti.Maintenance.Infrastructure\Backend\Cleanup.ps1'
$tokens=$null; $parseErrors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($source,[ref]$tokens,[ref]$parseErrors)
if ($parseErrors) { throw ($parseErrors | Out-String) }
$definition=$ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Invoke-Cleanup'},$true)
. ([scriptblock]::Create($definition.Extent.Text))
$script:CleanupNames=@('Memory Dump Files','Update Cleanup')
$script:CompactOutput=$true; $script:Admin=$true; $script:Killed=0
function Section([string]$Text) { }
function Get-ChildItem { param($LiteralPath) @([pscustomobject]@{PSPath='fixture:good';PSChildName='Memory Dump Files'},[pscustomobject]@{PSPath='fixture:timeout';PSChildName='Update Cleanup'}) }
function Get-Process { param($Name,$ErrorAction) }
function Get-WmiObject { param($Class,$Filter) [pscustomobject]@{DeviceID='C:';FreeSpace=10GB} }
function Test-Path { param($LiteralPath) $true }
function Get-ItemProperty { param($LiteralPath,$Name,$ErrorAction) }
function New-ItemProperty { param($LiteralPath,$Name,$PropertyType,$Value,$ErrorAction) $script:Category=$LiteralPath }
function Remove-ItemProperty { param($LiteralPath,$Name,$ErrorAction) }
function Start-Process {
    param($FilePath,$ArgumentList,$WindowStyle,[switch]$PassThru)
    if ($WindowStyle -ne 'Hidden') { throw 'Cleanup must remain hidden.' }
    $process=[pscustomobject]@{ExitCode=0;Timeout=($script:Category -eq 'fixture:timeout')}
    $process | Add-Member ScriptMethod WaitForExit {param($milliseconds) if($milliseconds -le 0 -or $milliseconds -gt 600000){throw 'Unbounded wait'}; return !$this.Timeout}
    $process | Add-Member ScriptMethod Kill { $script:Killed++ }
    $process | Add-Member ScriptMethod Dispose { }
    return $process
}
$message=''
try { Invoke-Cleanup } catch { $message=$_.Exception.Message }
if ($script:CleanupResult.Categories.Count -ne 2 -or $script:CleanupResult.ExitCode -ne 1 -or
    $script:CleanupResult.Categories[0].State -ne 'Completed' -or $script:Killed -ne 1 -or
    $message -notmatch 'Update Cleanup') { throw 'Cleanup failed to isolate a stalled category or preserve partial success.' }
Write-Output 'PASS: cleanup categories have bounded waits; a stalled handler is stopped and partial success is retained.'
