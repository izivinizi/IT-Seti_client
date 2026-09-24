$ErrorActionPreference = 'Stop'
$data = Join-Path $env:ProgramData 'ITSeti\Maintenance\Repairs'
$root = Join-Path $data ([guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root -Force | Out-Null
[IO.File]::WriteAllText((Join-Path $data 'latest.txt'), $root, [Text.Encoding]::UTF8)
try {
    foreach ($name in @('Repair.ps1', 'RepairWorker.ps1')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $root -Force
    }
    & (Join-Path $root 'RepairWorker.ps1') -JobRoot $root
} catch {
    [IO.File]::WriteAllText((Join-Path $root 'status.txt'), ('Ошибка: ' + $_.Exception.Message), [Text.Encoding]::UTF8)
} finally {
    [IO.File]::WriteAllText((Join-Path $root 'finished.txt'), [DateTimeOffset]::Now.ToString('o'), [Text.Encoding]::UTF8)
}
