param([string]$JobRoot)
$ErrorActionPreference = 'Stop'
try {
    & (Join-Path $JobRoot 'RepairWorker.ps1') -JobRoot $JobRoot
} catch {
    [IO.File]::WriteAllText((Join-Path $JobRoot 'status.txt'), ('Ошибка: ' + $_.Exception.Message), [Text.Encoding]::UTF8)
} finally {
    [IO.File]::WriteAllText((Join-Path $JobRoot 'finished.txt'), [DateTimeOffset]::Now.ToString('o'), [Text.Encoding]::UTF8)
}
