param([string]$Root = (Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference='Stop'
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('ITSeti-enrollment-'+[Guid]::NewGuid().ToString('N'))
$directory=Join-Path $fixture 'ITSeti\Maintenance'
[void][IO.Directory]::CreateDirectory($directory)
try {
    [IO.File]::WriteAllText((Join-Path $directory 'server-device.json'),(@{serverUrl='https://it-seti.nylenz.ru';deviceId=[Guid]::NewGuid().ToString();deviceKey='fixture-not-a-secret';companyId=1;siteId=1}|ConvertTo-Json))
    $start=New-Object Diagnostics.ProcessStartInfo
    $start.FileName=Join-Path $PSHOME 'powershell.exe'
    $start.Arguments='-STA -NoProfile -ExecutionPolicy Bypass -File "'+(Join-Path (Resolve-Path $Root).Path 'Connect-Server.ps1')+'" -SkipIfConnected'
    $start.UseShellExecute=$false
    $start.CreateNoWindow=$true
    $start.EnvironmentVariables['ProgramData']=$fixture
    $process=[Diagnostics.Process]::Start($start)
    try {
        if(!$process.WaitForExit(5000)){$process.Kill();throw 'Existing enrollment unexpectedly opened a dialog.'}
        if($process.ExitCode -ne 0){throw 'Existing enrollment failed.'}
    } finally {$process.Dispose()}
    'PASS: connected-PC setup exits without UI, password prompt or network request.'
} finally {
    [IO.File]::Delete((Join-Path $directory 'server-device.json'))
    [IO.Directory]::Delete($directory)
    [IO.Directory]::Delete((Join-Path $fixture 'ITSeti'))
    [IO.Directory]::Delete($fixture)
}
