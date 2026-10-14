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

$sourceRoot=$Root
$catalogSaver=@($sendAst.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Save-SoftwareCatalogResponse'},$true))
if($catalogSaver.Count -ne 1){throw 'Report response catalog cache writer is missing or duplicated.'}
. ([scriptblock]::Create($catalogSaver[0].Extent.Text))
$catalogFixture=Join-Path $env:TEMP ('ITSeti-report-catalog-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $catalogFixture -Force | Out-Null
try {
    $root=$catalogFixture
    $entry=[pscustomobject]@{id=[Guid]::NewGuid();key='rms';platform='windows';sha256=('a'*64);sizeBytes=123;fileName='rms.msi';version='7.7.3';name='RMS'}
    Save-SoftwareCatalogResponse ([pscustomobject]@{softwareCatalog=@($entry)})
    $cache=Join-Path $catalogFixture 'software-catalog.json'
    $saved=@((ConvertFrom-Json -InputObject ([IO.File]::ReadAllText($cache,[Text.Encoding]::UTF8))))
    if($saved.Count -ne 1 -or $saved[0].key -ne 'rms'){throw 'Valid report catalog was not cached.'}
    $original=[IO.File]::ReadAllText($cache,[Text.Encoding]::UTF8)
    $entry.fileName='../outside.exe'
    $rejected=$false
    try{Save-SoftwareCatalogResponse ([pscustomobject]@{softwareCatalog=@($entry)})}catch{$rejected=$true}
    if(!$rejected -or [IO.File]::ReadAllText($cache,[Text.Encoding]::UTF8) -ne $original -or
       (Get-ChildItem -LiteralPath $catalogFixture -Filter '*.pending').Count){
        throw 'Invalid report catalog modified the prior cache or left a partial file.'
    }
    Save-SoftwareCatalogResponse ([pscustomobject]@{softwareCatalog=$null})
    if([IO.File]::ReadAllText($cache,[Text.Encoding]::UTF8) -ne $original){throw 'A catalog-less report response overwrote the last known catalog.'}
} finally {
    Remove-Item -LiteralPath $catalogFixture -Recurse -Force
    $Root=$sourceRoot
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
if(!$coordinator.Contains('now - lastUpload < TimeSpan.FromHours(1)') -or !$coordinator.Contains('persistedAttempt') -or
   !$coordinator.Contains('TryStartReportUpload();') -or
   !$coordinator.Contains('reportUploadGate') -or
   $coordinator.Contains('lastCatalogRefresh') -or !$upload.Contains('-AutoUpdate -UseCachedCatalog')){
    throw 'The service must limit report uploads to once per hour across diagnostic completions and the timer.'
}
$serverPath=Join-Path (Split-Path $Root -Parent) 'ITSeti.Server\Program.cs'
$server=[IO.File]::ReadAllText($serverPath,[Text.Encoding]::UTF8)
if(!$server.Contains('includeCatalog ? await ReadLatestSoftwareCatalogAsync') -or
   !$server.Contains('return Results.Ok(await ReadLatestSoftwareCatalogAsync') -or
   !$upload.Contains('Receive-SoftwareCatalog') -or !$upload.Contains('includeCatalog=')){
    throw 'Report responses and the catalog endpoint must expose the same latest software catalog.'
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
