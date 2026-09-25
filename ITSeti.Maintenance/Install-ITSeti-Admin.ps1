$ErrorActionPreference = 'Stop'
$setup = Join-Path $PSScriptRoot 'ITSeti-Maintenance-Setup.exe'
try {
    if (!(Test-Path -LiteralPath $setup -PathType Leaf)) { throw 'Installer EXE is missing.' }
    $installer = Start-Process -FilePath $setup -Verb RunAs -Wait -PassThru -ErrorAction Stop
    exit $installer.ExitCode
}
catch {
    $exception = $_.Exception.GetBaseException()
    Write-Host ('Administrator launch failed: ' + $exception.Message) -ForegroundColor Red
    if ($exception -is [ComponentModel.Win32Exception] -and $exception.NativeErrorCode -eq 1223) { exit 1223 }
    exit 9001
}
