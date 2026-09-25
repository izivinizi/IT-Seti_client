$ErrorActionPreference='Stop'
$base=Join-Path $env:ProgramData 'ITSeti\Maintenance'
$requestRoot=Join-Path $base 'OrganizationSetupRequests'
$resultRoot=Join-Path $base 'OrganizationSetupRuns'
$helper=Join-Path $PSScriptRoot 'Install-OrganizationSoftware.ps1'
if(!(Test-Path -LiteralPath $helper -PathType Leaf)){throw 'Trusted organization setup helper is missing.'}
$request=Get-ChildItem -LiteralPath $requestRoot -Filter '*.json' -File -ErrorAction SilentlyContinue |
    Where-Object { $_.BaseName -match '^[a-f0-9]{32}$' } | Sort-Object CreationTimeUtc | Select-Object -First 1
if(!$request){exit 0}
if($request.Length -gt 4096){Remove-Item -LiteralPath $request.FullName -Force; throw 'Organization setup request is too large.'}
$id=$request.BaseName
$run=Join-Path $resultRoot $id
New-Item -ItemType Directory -Path $run -Force | Out-Null
$result=Join-Path $run 'result.txt'
$temporary=Join-Path $run 'result.pending'
try {
    $payload=Get-Content -LiteralPath $request.FullName -Raw | ConvertFrom-Json
    $source=[IO.Path]::GetFullPath([string]$payload.Source)
    if(!$source -or $source.Contains('"') -or !(Test-Path -LiteralPath (Join-Path $source 'system\Install.ps1') -PathType Leaf)){
        throw 'Selected ITSETI-Setup folder is unavailable.'
    }
    Remove-Item -LiteralPath $request.FullName -Force
    $resultFromHelper=Join-Path $run 'installer-result.txt'
    $powershell=Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $arguments='-NoProfile -ExecutionPolicy Bypass -File "'+$helper+'" -InstallerDirectory "'+$source+'" -ResultFile "'+$resultFromHelper+'"'
    $process=Start-Process -FilePath $powershell -ArgumentList $arguments -WorkingDirectory $PSScriptRoot -WindowStyle Hidden -PassThru -Wait -ErrorAction Stop
    $detail=if(Test-Path -LiteralPath $resultFromHelper){(Get-Content -LiteralPath $resultFromHelper -Raw).Trim()}else{"Installer helper exited with code $($process.ExitCode) without a result."}
    if($process.ExitCode -eq 0 -and $detail -eq 'OK'){$detail='OK'}
    elseif(!$detail){$detail="Installer helper exited with code $($process.ExitCode)."}
    [IO.File]::WriteAllText($temporary,$detail,[Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporary -Destination $result -Force
} catch {
    [IO.File]::WriteAllText($temporary,$_.Exception.ToString(),[Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporary -Destination $result -Force
    exit 1
}
