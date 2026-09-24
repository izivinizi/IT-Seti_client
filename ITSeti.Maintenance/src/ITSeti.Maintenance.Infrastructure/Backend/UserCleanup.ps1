function Start-UserCleanupWaiting([switch]$TestOnly) {
    $folder=Join-Path (Join-Path $env:LOCALAPPDATA 'ServiceMaintenance\UserJobs') ([guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $folder -Force -ErrorAction Stop | Out-Null
    Copy-Item -LiteralPath (Join-Path $ScriptRoot 'UserCleanupWorker.ps1') -Destination $folder -ErrorAction Stop
    $b64=[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($folder))
    $command="`$r=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('$b64')); & ([scriptblock]::Create([IO.File]::ReadAllText((Join-Path `$r 'UserCleanupWorker.ps1'),[Text.Encoding]::UTF8))) -JobRoot `$r"
    if($TestOnly){$command += ' -TestOnly'}
    $encoded=[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
    Start-Process powershell.exe -WindowStyle Hidden -WorkingDirectory $folder -ArgumentList "-NoProfile -ExecutionPolicy Bypass -EncodedCommand $encoded" -ErrorAction Stop | Out-Null
    return $folder
}
