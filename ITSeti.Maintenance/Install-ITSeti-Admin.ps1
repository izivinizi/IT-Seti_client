$ErrorActionPreference = 'Stop'
$setup = Join-Path $PSScriptRoot 'ITSeti-Maintenance-Setup.exe'
try {
    if (!(Test-Path -LiteralPath $setup -PathType Leaf)) { throw 'Installer EXE is missing.' }
    $installer = Start-Process -FilePath $setup -Verb RunAs -Wait -PassThru -ErrorAction Stop
    exit $installer.ExitCode
}
catch {
    Write-Host ('Administrator launch failed: ' + $_.Exception.Message) -ForegroundColor Red
    exit 1
}
