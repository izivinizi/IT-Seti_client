param([string]$Root = (Split-Path $PSScriptRoot -Parent), [string]$OutputPath)
$ErrorActionPreference = 'Stop'
$binaryRoot = Join-Path $Root 'src\ITSeti.Maintenance.Win7.Port\bin\Release\net48'
[void][Reflection.Assembly]::LoadFrom((Join-Path $binaryRoot 'ITSeti.Maintenance.Core.dll'))
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $binaryRoot 'ITSeti.Maintenance.Win7.Port.exe'))
Add-Type -AssemblyName System.Web.Extensions
$snapshot = New-Object ITSeti.Maintenance.Win7.LegacySnapshot
$snapshot.Id = [Guid]::NewGuid().ToString('D')
$snapshot.StartedAt = [DateTimeOffset]::UtcNow.ToString('O')
$snapshot.ComputerName = 'WIN7-FIXTURE'
$snapshot.CpuTemperatureC = 90
$snapshot.TotalMemoryGb = 16
$snapshot.FreeMemoryGb = 8
$snapshot.CpuPercent = 20
$snapshot.HwInfoVersion = '8.54'
$snapshot.HwInfoCapturedAtUtc = $snapshot.StartedAt
$adapter = $assembly.GetType('ITSeti.Maintenance.Win7.LegacyDiagnosticAdapter', $true)
$flags = [Reflection.BindingFlags]'Static,NonPublic'
$fields = $adapter.GetMethod('ReportFields', $flags).Invoke($null, @($snapshot.PSObject.BaseObject))
$serializer = New-Object System.Web.Script.Serialization.JavaScriptSerializer
$json = $serializer.Serialize($fields)
$parsed = $json | ConvertFrom-Json
if ($parsed.DiagnosticResult.RulesVersion -ne '2.4.1' -or
    $parsed.DiagnosticSnapshot.StartedAt -isnot [string] -or
    @($parsed.DiagnosticResult.Issues | Where-Object Severity -eq 'Critical').Count -ne 1) {
    throw 'Win7 shared engine or ISO report contract failed.'
}
# Test the same queue implementation compiled for .NET Framework, without launching workers.
$queueType = [ITSeti.Maintenance.Core.OperationQueue[string]]
$queue = [Activator]::CreateInstance($queueType)
[void]$queue.Enqueue('check', 'maintenance', $false)
[void]$queue.Enqueue('cleanup', 'maintenance', $false)
if (!$queue.TryStart('check') -or $queue.TryStart('cleanup')) { throw 'Win7 queue overlaps workers.' }
[void]$queue.Cancel('check')
if ($queue.TryStart('cleanup')) { throw 'Win7 cancelling worker released its slot early.' }
$queue.Complete('check', $null)
if (!$queue.TryStart('cleanup')) { throw 'Win7 queue did not advance.' }
if ($OutputPath) { [IO.File]::WriteAllText([IO.Path]::GetFullPath($OutputPath), $json, [Text.UTF8Encoding]::new($false)) }
'PASS Win7 net48: shared diagnostics, ISO timestamps, JSON contract, queue cancellation/exclusion'
