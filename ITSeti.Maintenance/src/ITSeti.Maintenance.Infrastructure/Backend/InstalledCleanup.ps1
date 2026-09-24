$ErrorActionPreference = 'Stop'
$base = Join-Path $env:ProgramData 'ITSeti\Maintenance'
$queue = Join-Path $base 'CleanupRequests'
$worker = Join-Path $PSScriptRoot 'AppCleanupWorker.ps1'
foreach ($request in @(Get-ChildItem -LiteralPath $queue -Filter '*.request' -File -ErrorAction Stop | Sort-Object CreationTimeUtc)) {
    $job = $null
    $run = $null
    try {
        if ($request.BaseName -notmatch '^[a-f0-9]{32}$') { throw 'Invalid cleanup job identifier.' }
        $run = Join-Path (Join-Path $base 'CleanupRuns') $request.BaseName
        New-Item -ItemType Directory -Path $run -Force | Out-Null
        if (Test-Path -LiteralPath (Join-Path $run 'admin-finished.txt')) { continue }
        $job = [IO.Path]::GetFullPath(([IO.File]::ReadAllText($request.FullName)).Trim())
        if ($job -notmatch '\\ServiceMaintenance\\UserJobs\\[a-f0-9]{32}$') { throw 'Invalid cleanup job path.' }
        if ((Split-Path $job -Leaf) -ne $request.BaseName) { throw 'Cleanup job identifier mismatch.' }
        $node = Get-Item -LiteralPath $job -ErrorAction Stop
        while ($node) { if ($node.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Redirected cleanup job path is not allowed.' }; $node = $node.Parent }
        if (!(Test-Path -LiteralPath (Join-Path $job 'ready.txt') -PathType Leaf)) { throw 'User cleanup worker is not ready.' }
        & $worker -JobRoot $job -TrustedScriptsRoot $PSScriptRoot -StatusRoot $run
    } catch {
        if ($run -and (Test-Path -LiteralPath $run -PathType Container)) {
            try { [IO.File]::WriteAllText((Join-Path $run 'admin-stage.txt'), ('System cleanup failed: ' + $_.Exception.Message), [Text.Encoding]::UTF8) } catch { }
        }
    } finally {
        if ($run -and (Test-Path -LiteralPath $run -PathType Container)) {
            try { [IO.File]::WriteAllText((Join-Path $run 'admin-finished.txt'), [DateTimeOffset]::Now.ToString('o'), [Text.Encoding]::UTF8) } catch { }
        }
        Remove-Item -LiteralPath $request.FullName -Force -ErrorAction SilentlyContinue
    }
}
