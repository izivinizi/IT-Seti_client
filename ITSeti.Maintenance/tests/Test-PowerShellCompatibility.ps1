param([string]$Root = (Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference = 'Stop'
$files = @(Get-ChildItem -LiteralPath $Root -Filter '*.ps1' -File)
$files += @(Get-ChildItem -LiteralPath (Join-Path $Root 'src') -Filter '*.ps1' -File -Recurse | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' })
$failures = @()
foreach ($file in $files) {
    $bytes = [IO.File]::ReadAllBytes($file.FullName)
    $hasNonAscii = @($bytes | Where-Object { $_ -gt 127 }).Count -gt 0
    if ($hasNonAscii -and !($bytes.Length -ge 3 -and $bytes[0] -eq 239 -and $bytes[1] -eq 187 -and $bytes[2] -eq 191)) {
        $failures += $file.FullName + ': non-ASCII script requires a UTF-8 BOM for Windows PowerShell.'
    }
    $tokens = $null
    $parseErrors = $null
    [void][Management.Automation.Language.Parser]::ParseFile($file.FullName, [ref]$tokens, [ref]$parseErrors)
    foreach ($failure in $parseErrors) { $failures += $file.FullName + ': ' + $failure.Message }
}
if ($failures.Count) { throw ($failures -join [Environment]::NewLine) }
Write-Output ('PASS: {0} scripts parsed by PowerShell {1}; encoding is compatible with Windows PowerShell.' -f $files.Count, $PSVersionTable.PSVersion)
