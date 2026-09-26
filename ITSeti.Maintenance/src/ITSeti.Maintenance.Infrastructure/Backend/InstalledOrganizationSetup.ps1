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
    $component=[string]$payload.Component
    if($component -and $component -notin @('AnyDesk','RMS','OCS','Panel')){throw 'Unsupported organization software component.'}
    if(!$source -or $source.Contains('"') -or !(Test-Path -LiteralPath (Join-Path $source 'system\Install.ps1') -PathType Leaf)){
        throw 'Selected ITSETI-Setup folder is unavailable.'
    }
    Remove-Item -LiteralPath $request.FullName -Force
    $sourcePackages=Join-Path $source 'system\packages'
    $stagedRoot=Join-Path $run 'ITSETI-Setup'
    $stagedSystem=Join-Path $stagedRoot 'system'
    $stagedPackages=Join-Path $stagedSystem 'packages'
    New-Item -ItemType Directory -Path $stagedPackages -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $source 'system\Install.ps1') -Destination (Join-Path $stagedSystem 'Install.ps1') -Force
    $packageMap=@{
        AnyDesk='AnyDesk-installer.exe'
        RMS='Host-IT-SETI.RMS.7.7.3.0v3.msi'
        OCS='OCS-Agent-Installerv4.exe'
        Panel='DesktopInfo3230.exe'
    }
    $packages=if($component){@($packageMap[$component])}else{@($packageMap.Values)}
    foreach($name in $packages){Copy-Item -LiteralPath (Join-Path $sourcePackages $name) -Destination (Join-Path $stagedPackages $name) -Force}
    if(!$component -or $component -eq 'Panel'){
        $stagedPanel=Join-Path $stagedSystem 'panel'
        New-Item -ItemType Directory -Path $stagedPanel -Force | Out-Null
        foreach($name in @('DesktopInfo.ini','update-support-ids.ps1','start-panel.vbs')){
            Copy-Item -LiteralPath (Join-Path $source ('system\panel\'+$name)) -Destination (Join-Path $stagedPanel $name) -Force
        }
    }
    $resultFromHelper=Join-Path $run 'installer-result.txt'
    $powershell=Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $arguments='-NoProfile -ExecutionPolicy Bypass -File "'+$helper+'" -InstallerDirectory "'+$stagedRoot+'" -ResultFile "'+$resultFromHelper+'"'
    if($component){$arguments+=' -Component '+$component}
    $process=Start-Process -FilePath $powershell -ArgumentList $arguments -WorkingDirectory $PSScriptRoot -WindowStyle Hidden -PassThru -Wait -ErrorAction Stop
    $detail=if(Test-Path -LiteralPath $resultFromHelper){(Get-Content -LiteralPath $resultFromHelper -Raw).Trim()}else{"Installer helper exited with code $($process.ExitCode) without a result."}
    if($process.ExitCode -eq 0 -and $detail -eq 'OK'){$detail='OK'}
    elseif(!$detail){$detail="Installer helper exited with code $($process.ExitCode)."}
    [IO.File]::WriteAllText($temporary,$detail,[Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporary -Destination $result -Force
    Remove-Item -LiteralPath $stagedRoot -Recurse -Force -ErrorAction SilentlyContinue
} catch {
    [IO.File]::WriteAllText($temporary,$_.Exception.ToString(),[Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporary -Destination $result -Force
    if($stagedRoot -and $stagedRoot.StartsWith($run,[StringComparison]::OrdinalIgnoreCase)){
        Remove-Item -LiteralPath $stagedRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
    exit 1
}
