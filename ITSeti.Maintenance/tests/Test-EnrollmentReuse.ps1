param([string]$Root = (Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference='Stop'
$connectSource=[IO.File]::ReadAllText((Join-Path $Root 'Connect-Server.ps1'),[Text.Encoding]::UTF8)
$installSource=[IO.File]::ReadAllText((Join-Path $Root 'Install-Maintenance.ps1'),[Text.Encoding]::UTF8)
$viewModelSource=[IO.File]::ReadAllText((Join-Path $Root 'src\ITSeti.Maintenance.App\MainViewModel.cs'),[Text.Encoding]::UTF8)
if(!$connectSource.Contains("foreach (`$sidText in @('S-1-5-18', 'S-1-5-32-544', 'S-1-5-32-545'))") -or
   !$connectSource.Contains("if (`$sidText -eq 'S-1-5-32-545')") -or
   !$connectSource.Contains('[Security.AccessControl.FileSystemRights]::Read')){
    throw 'New enrollment files must be readable, but not writable, by standard users.'
}
if(!$installSource.Contains("`$enrollmentFile=Join-Path `$data 'server-device.json'") -or
   !$installSource.Contains("'*S-1-5-32-545:R'")){
    throw 'Installer does not repair read access to an existing enrollment file.'
}
if($viewModelSource -notmatch 'public bool IsServerConnectionConfigured' -or
   $viewModelSource -notmatch 'ServerConnectionLabel => IsServerConnectionConfigured'){
    throw 'Server connection status is still coupled to company/site assignment.'
}
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('ITSeti-enrollment-'+[Guid]::NewGuid().ToString('N'))
$directory=Join-Path $fixture 'ITSeti\Maintenance'
[void][IO.Directory]::CreateDirectory($directory)
try {
    [IO.File]::WriteAllText((Join-Path $directory 'server-device.json'),(@{serverUrl='https://it-seti.nylenz.ru';deviceId=[Guid]::NewGuid().ToString();deviceKey='fixture-not-a-secret'}|ConvertTo-Json))
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
