param([string]$Root)
$ErrorActionPreference='Stop'
$uploadPath=Join-Path $Root 'src\ITSeti.Maintenance.Infrastructure\Backend\Upload-Reports.ps1'
$upload=[IO.File]::ReadAllText($uploadPath,[Text.Encoding]::UTF8)
$tokens=$null;$errors=$null
[void][Management.Automation.Language.Parser]::ParseFile($uploadPath,[ref]$tokens,[ref]$errors)
if($errors){throw 'Application-error upload script has PowerShell syntax errors.'}
$sendAst=[Management.Automation.Language.Parser]::ParseInput($upload,[ref]$tokens,[ref]$errors)
$send=@($sendAst.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Send-ApplicationErrors'},$true))
if($send.Count -ne 1){throw 'Application-error sender is missing or duplicated.'}
$sendSource=$send[0].Extent.Text
$responseIndex=$sendSource.IndexOf('$response = [Net.HttpWebResponse]$request.GetResponse()', [StringComparison]::Ordinal)
$deleteIndex=$sendSource.IndexOf('Remove-Item -LiteralPath $file.FullName -Force', [StringComparison]::Ordinal)
if($responseIndex -lt 0 -or $deleteIndex -le $responseIndex -or
   !$sendSource.Contains("'/api/v1/application-errors'") -or
   !$sendSource.Contains("'X-Device-Key'") -or
   !$upload.Contains('Send-ApplicationErrors $device')){
    throw 'Error outbox must be authenticated and entries removed only after a successful server response.'
}
$coordinator=[IO.File]::ReadAllText((Join-Path $Root 'src\ITSeti.Maintenance.Service\MaintenanceServiceCoordinator.cs'),[Text.Encoding]::UTF8)
$errorStore=[IO.File]::ReadAllText((Join-Path $Root 'src\ITSeti.Maintenance.Infrastructure\ApplicationErrorStore.cs'),[Text.Encoding]::UTF8)
if(!$errorStore.Contains('UnauthorizedAccessException access => "Windows запретила доступ: " + access.Message')) {
    throw 'Windows access-denied errors must show the denied path and native message in the dialog summary.'
}
$diagnosticRunner=[IO.File]::ReadAllText((Join-Path $Root 'src\ITSeti.Maintenance.Infrastructure\FullDiagnosticsRunner.cs'),[Text.Encoding]::UTF8)
$fullCheckWorker=[IO.File]::ReadAllText((Join-Path $Root 'src\ITSeti.Maintenance.Infrastructure\Backend\FullCheckWorker.ps1'),[Text.Encoding]::UTF8)
if(!$diagnosticRunner.Contains('WorkingDirectory = Path.GetDirectoryName(powershell)!') -or
   !$fullCheckWorker.Contains("WorkingDirectory (Join-Path `$env:WINDIR 'System32\WindowsPowerShell\v1.0')") -or
   $fullCheckWorker -match 'Start-Process powershell\.exe[^\r\n]*WorkingDirectory \$RunRoot') {
    throw 'Full diagnostic PowerShell processes must not start inside a potentially inaccessible per-user report directory.'
}
if(!$coordinator.Contains('lastUpload >= TimeSpan.FromMinutes(5)') -or
   !$coordinator.Contains('StartOperation(MaintenanceServiceOperation.UploadReports)')){
    throw 'The service must retry the application-error outbox while the client UI is closed.'
}
$workerPath=Join-Path $Root 'src\ITSeti.Maintenance.Infrastructure\Backend\FullCheckWorker.ps1'
$worker=[IO.File]::ReadAllText($workerPath,[Text.Encoding]::UTF8)
$workerAst=[Management.Automation.Language.Parser]::ParseFile($workerPath,[ref]$tokens,[ref]$errors)
if($errors){throw 'Full diagnostic worker has PowerShell syntax errors.'}
$fallback=@($workerAst.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-ActiveUptimeHoursFallback'},$true))
if($fallback.Count -ne 1){throw 'Uptime fallback helper is missing or duplicated.'}
. ([scriptblock]::Create($fallback[0].Extent.Text))
$boot=[Management.ManagementDateTimeConverter]::ToDmtfDateTime([DateTime]::Now.AddHours(-4))
$hours=Get-ActiveUptimeHoursFallback ([pscustomobject]@{LastBootUpTime=$boot})
if($null -eq $hours -or [math]::Abs($hours-4) -gt 0.2){throw "Uptime fallback returned invalid hours: $hours"}
$summary=[IO.File]::ReadAllText((Join-Path $Root 'src\ITSeti.Maintenance.Infrastructure\Backend\Summary.ps1'),[Text.Encoding]::UTF8)
if(!$summary.Contains('Get-Command Get-ActiveUptimeHoursFallback') -or
   !$summary.Contains('Get-ActiveUptimeHoursFallback $os') -or
   !$summary.Contains("Время работы рассчитано резервным способом") -or
   !$summary.Contains("Время работы: ") -or
   $summary.IndexOf('$s.TotalRAM=',[StringComparison]::Ordinal) -lt $summary.IndexOf('$s.ActiveUptimeHours=Get-ActiveUptimeHours $os',[StringComparison]::Ordinal)){
    throw 'Missing uptime helper must use the fallback without aborting CPU/RAM collection.'
}
Write-Output 'PASS application-error outbox auth/retry and uptime fallback for mixed runtime script versions'
