param(
    [string]$DiskSpdPath = (Join-Path $env:ProgramFiles 'ITSeti Maintenance\Tools\CrystalDiskMark9\CdmResource\DiskSpd\DiskSpd64.exe'),
    [ValidateRange(2, 10)][int]$DurationSeconds = 3
)

$ErrorActionPreference = 'Stop'
$workerPath = Join-Path (Split-Path $PSScriptRoot -Parent) 'src\ITSeti.Maintenance.Infrastructure\Backend\HeadlessDiskWorker.ps1'
if (!(Test-Path -LiteralPath $DiskSpdPath -PathType Leaf)) { throw "DiskSpd was not found: $DiskSpdPath" }
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile([IO.Path]::GetFullPath($workerPath), [ref]$tokens, [ref]$errors)
if ($errors) { throw ($errors | Out-String) }
foreach ($definition in $ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] }, $true)) {
    . ([scriptblock]::Create($definition.Extent.Text))
}

$tempBase = [IO.Path]::GetFullPath($env:TEMP).TrimEnd('\')
$tempRoot = Join-Path $tempBase ('ITSeti-disk-latency-' + [guid]::NewGuid().ToString('N'))
$target = Join-Path $tempRoot 'latency.dat'
New-Item -ItemType Directory -Path $tempRoot | Out-Null
function Invoke-LiveDiskPass([string]$Mode, [switch]$Measure) {
    $before = if ($Measure) { Get-SystemDrivePerfCounter } else { $null }
    $write = if ($Mode -eq 'write') { '-w100' } else { '-w0' }
    $xml = Join-Path $tempRoot ($Mode + '.xml')
    $stderr = Join-Path $tempRoot ($Mode + '.stderr')
    $arguments = @('-c32M', '-b64K', '-t1', '-o1', '-s', $write, '-Sh', '-W1',
        ('-d' + $DurationSeconds), '-C0', '-Rxml', $target)
    $process = Start-Process -FilePath $DiskSpdPath -WorkingDirectory (Split-Path $DiskSpdPath) `
        -ArgumentList $arguments -RedirectStandardOutput $xml -RedirectStandardError $stderr -PassThru -Wait
    $errorText = if (Test-Path -LiteralPath $stderr) { [IO.File]::ReadAllText($stderr) } else { '' }
    if (!(Test-DiskSpdExit $process.ExitCode $errorText)) {
        throw "DiskSpd $Mode fixture failed (code $($process.ExitCode)): $errorText"
    }
    if (!$Measure) { return $null }
    $after = Get-SystemDrivePerfCounter
    $measurement = Measure-DiskLatencyDelta $before $after $Mode
    if (!$measurement -or $measurement.Operations -lt 10 -or $measurement.LatencyMs -le 0) {
        throw "No valid system disk $Mode latency sample; counter operations were $($measurement.Operations)."
    }
    return $measurement
}

try {
    # The first write creates the small test file; only subsequent passes are measured.
    Invoke-LiveDiskPass 'write' | Out-Null
    $read = Invoke-LiveDiskPass 'read' -Measure
    $write = Invoke-LiveDiskPass 'write' -Measure
    Write-Output ('PASS live raw system-disk counters: read={0:N3} ms / {1} operations; write={2:N3} ms / {3} operations.' -f
        $read.LatencyMs, $read.Operations, $write.LatencyMs, $write.Operations)
}
finally {
    $resolved = [IO.Path]::GetFullPath($tempRoot)
    if (!$resolved.StartsWith($tempBase + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Refusing to remove a latency fixture outside TEMP.'
    }
    if (Test-Path -LiteralPath $resolved) {
        foreach ($file in Get-ChildItem -LiteralPath $resolved -File) {
            Remove-Item -LiteralPath $file.FullName -Force
        }
        [IO.Directory]::Delete($resolved)
    }
}
