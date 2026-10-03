param([string]$Root = (Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference='Stop'
. (Join-Path $Root 'src\ITSeti.Maintenance.Infrastructure\Backend\ScheduledFull.ps1')
$testRoot=Join-Path $env:TEMP ('ITSeti-scheduled-full-'+[guid]::NewGuid().ToString('N'))
$runs=Join-Path $testRoot 'Runs'
New-Item -ItemType Directory -Path $runs -Force | Out-Null
try {
    $now=[DateTimeOffset]::UtcNow
    $baseline=Initialize-ScheduledFullBaseline $testRoot $now
    if(($baseline-$now).Duration() -gt [TimeSpan]::FromSeconds(1)){throw 'New installation baseline is wrong.'}
    if(Test-ScheduledFullDue $testRoot $now.AddDays(59)){throw 'First scheduled check started before 60 days.'}
    if(!(Test-ScheduledFullDue $testRoot $now.AddDays(60))){throw 'Scheduled check did not start at 60 days.'}

    $run=Join-Path $runs ([guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $run | Out-Null
    [IO.File]::WriteAllText((Join-Path $run 'result.json'),'{}')
    $success=Join-Path $testRoot 'success.ps1'
    [IO.File]::WriteAllText($success,'param([string]$ResultFile); [IO.File]::WriteAllText($ResultFile,''{"Success":true,"Status":"completed"}'')')
    Complete-ScheduledFullCheck $testRoot $run $success
    if(Test-ScheduledFullDue $testRoot ([DateTimeOffset]::UtcNow.AddDays(59))){throw 'Successful repair did not reset the 60-day timer.'}

    Set-ScheduledFullBaseline $testRoot $now.AddDays(-61)
    $failure=Join-Path $testRoot 'failure.ps1'
    [IO.File]::WriteAllText($failure,'param([string]$ResultFile); [IO.File]::WriteAllText($ResultFile,''{"Success":false,"Status":"failed"}'')')
    $failed=$false
    try {Complete-ScheduledFullCheck $testRoot $run $failure} catch {$failed=$true}
    if(!$failed -or !(Test-ScheduledFullDue $testRoot $now)){throw 'Failed repair incorrectly marked the full check complete.'}

    Remove-Item -LiteralPath (Join-Path $testRoot 'last-full-run.txt') -Force
    $legacy=Join-Path $runs ([guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $legacy | Out-Null
    $legacyResult=Join-Path $legacy 'result.json'
    [IO.File]::WriteAllText($legacyResult,'{}')
    [IO.File]::SetLastWriteTimeUtc($legacyResult,$now.AddDays(-7).UtcDateTime)
    [IO.File]::WriteAllText((Join-Path $testRoot 'latest-full.txt'),$legacy)
    if(Test-ScheduledFullDue $testRoot $now){throw 'A recent pre-upgrade full check was ignored.'}
    Write-Output 'PASS: 60-day gate, successful SYSTEM repair, failure retry and legacy full-check migration.'
} finally {
    Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
}
