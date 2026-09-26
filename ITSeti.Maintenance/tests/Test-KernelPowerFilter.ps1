param([string]$Root=(Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference='Stop'
$worker=Join-Path $Root 'src\ITSeti.Maintenance.Infrastructure\Backend\EventWorker.ps1'
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($worker,[ref]$tokens,[ref]$errors)
if($errors){throw 'Event worker has PowerShell syntax errors.'}
$function=$ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-KernelPowerBugcheckCode'},$true)
if(!$function){throw 'Kernel-Power XML reader is missing.'}
. ([scriptblock]::Create($function.Extent.Text))

function New-TestEvent([int]$Id,[string]$Provider,[string]$Xml) {
    $event=[pscustomobject]@{Id=$Id;ProviderName=$Provider;Xml=$Xml}
    $event | Add-Member -MemberType ScriptMethod -Name ToXml -Value { $this.Xml }
    return $event
}

$provider='Microsoft-Windows-Kernel-Power'
$zero=New-TestEvent 41 $provider '<Event><EventData><Data Name="BugcheckCode">0</Data></EventData></Event>'
$nonzero=New-TestEvent 41 $provider '<Event><EventData><Data Name="BugcheckCode">278</Data></EventData></Event>'
$other=New-TestEvent 12 'Other-Provider' '<Event><EventData><Data Name="BugcheckCode">0</Data></EventData></Event>'
if((Get-KernelPowerBugcheckCode $zero) -ne 0 -or
   (Get-KernelPowerBugcheckCode $nonzero) -ne 278 -or
   $null -ne (Get-KernelPowerBugcheckCode $other)) {
    throw 'Kernel-Power BugcheckCode parsing returned an unexpected value.'
}
'PASS: Kernel-Power BugcheckCode extracted from event XML.'
