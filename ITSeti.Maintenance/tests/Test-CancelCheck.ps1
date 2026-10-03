param([string]$Root,[ValidateSet('Debug','Release')][string]$Configuration='Release')
$ErrorActionPreference='Stop'
$bundle=Join-Path $Root "src\ITSeti.Maintenance.App\bin\$Configuration\net9.0-windows\Backend"
if(!(Test-Path -LiteralPath (Join-Path $bundle 'FullCheckWorker.ps1'))){throw 'Build the app before this test.'}
$run=Join-Path $Root ('artifacts\cancel-check-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $run -Force | Out-Null
Get-ChildItem -LiteralPath $bundle -File | Copy-Item -Destination $run
[IO.File]::WriteAllText((Join-Path $run 'cancel-request.txt'),'cancel',[Text.Encoding]::ASCII)
$worker=Join-Path $run 'FullCheckWorker.ps1'
& $worker -RunRoot $run -ToolsRoot '' -SkipDiskTests -SkipResourceSampling -LimitedMode
if(!(Test-Path -LiteralPath (Join-Path $run 'cancelled.txt'))){throw 'Cancellation marker missing.'}
if(Test-Path -LiteralPath (Join-Path $run 'result.json')){throw 'A cancelled check produced a completed result.'}
Remove-Item -LiteralPath (Join-Path $run 'cancelled.txt') -Force
[IO.File]::WriteAllText((Join-Path $run 'cancel-request.txt'),'owner:2147483647:1',[Text.Encoding]::ASCII)
& $worker -RunRoot $run -ToolsRoot '' -SkipDiskTests -SkipResourceSampling -LimitedMode
if(!(Test-Path -LiteralPath (Join-Path $run 'cancelled.txt'))){throw 'The worker did not stop after its owner process disappeared.'}
if(Test-Path -LiteralPath (Join-Path $run 'result.json')){throw 'An orphaned check produced a completed result.'}
Remove-Item -LiteralPath (Join-Path $run 'cancel-request.txt'),(Join-Path $run 'cancelled.txt') -Force
& $worker -RunRoot $run -ToolsRoot '' -SkipDiskTests -SkipResourceSampling -LimitedMode
if(!(Test-Path -LiteralPath (Join-Path $run 'result.json'))){throw 'A new check could not run after cancellation.'}
"PASS: button/forced-close cancellation left no result; the next check completed. $run"
