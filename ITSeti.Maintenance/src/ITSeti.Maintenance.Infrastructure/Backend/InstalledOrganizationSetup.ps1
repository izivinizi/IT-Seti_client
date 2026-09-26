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
    $payload=[IO.File]::ReadAllText($request.FullName,[Text.Encoding]::UTF8) | ConvertFrom-Json
    $component=[string]$payload.Component
    $operation=if($payload.Operation){[string]$payload.Operation}else{'Install'}
    if($component -and $component -notin @('AnyDesk','RMS','OCS','Panel','WinRAR','Yandex')){throw 'Unsupported organization software component.'}
    if($operation -notin @('Install','Uninstall')){throw 'Unsupported organization setup operation.'}
    $sourceValue=if($operation -eq 'Install'){[string]$payload.Source}else{''}
    $source=if($sourceValue){[IO.Path]::GetFullPath($sourceValue)}else{$null}
    if($operation -eq 'Uninstall'){
        if($component -in @('WinRAR','Yandex')){
            Remove-Item -LiteralPath $request.FullName -Force
            $programRoots=@($env:ProgramFiles,${env:ProgramFiles(x86)}) | Where-Object { $_ }
            if($component -eq 'WinRAR'){
                $uninstaller=$programRoots | ForEach-Object { Join-Path $_ 'WinRAR\Uninstall.exe' } |
                    Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
                $arguments=@('/S')
            }else{
                $uninstaller=$programRoots | ForEach-Object { Join-Path $_ 'Yandex\YandexBrowser\Application' } |
                    Where-Object { Test-Path -LiteralPath $_ -PathType Container } |
                    ForEach-Object { Get-ChildItem -LiteralPath $_ -Directory -ErrorAction SilentlyContinue |
                        Sort-Object Name -Descending | ForEach-Object { Join-Path $_.FullName 'Installer\setup.exe' } } |
                    Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
                $arguments=@('--uninstall','--system-level','--force-uninstall','--silent')
            }
            if(!$uninstaller){throw "$component machine-wide uninstaller was not found."}
            $process=Start-Process -FilePath $uninstaller -ArgumentList $arguments -WorkingDirectory (Split-Path -Parent $uninstaller) -WindowStyle Hidden -PassThru -Wait -ErrorAction Stop
            if($process.ExitCode -ne 0){throw "$component uninstaller exited with code $($process.ExitCode)."}
            [IO.File]::WriteAllText($temporary,'OK',[Text.UTF8Encoding]::new($false))
            Move-Item -LiteralPath $temporary -Destination $result -Force
            return
        }
        if(!$component){throw 'Choose one installed component to uninstall.'}
        $uninstallKey=if($component -eq 'Panel'){'DesktopInfo'}else{$component}
        $uninstaller=Join-Path (Split-Path -Parent $PSScriptRoot) 'OrganizationUninstall.ps1'
        if(!(Test-Path -LiteralPath $uninstaller -PathType Leaf)){throw 'Trusted organization uninstaller is missing.'}
        Remove-Item -LiteralPath $request.FullName -Force
        $resultFromHelper=Join-Path $run 'uninstaller-result.txt'
        $powershell=Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
        $arguments='-NoProfile -ExecutionPolicy Bypass -File "'+$uninstaller+'" -Only '+$uninstallKey+' -Silent -ResultFile "'+$resultFromHelper+'"'
        $process=Start-Process -FilePath $powershell -ArgumentList $arguments -WorkingDirectory $PSScriptRoot -WindowStyle Hidden -PassThru -Wait -ErrorAction Stop
        $detail=if(Test-Path -LiteralPath $resultFromHelper){[IO.File]::ReadAllText($resultFromHelper,[Text.Encoding]::UTF8).Trim()}else{"Uninstaller exited with code $($process.ExitCode) without a result."}
        if($process.ExitCode -ne 0){throw $detail}
        [IO.File]::WriteAllText($temporary,'OK',[Text.UTF8Encoding]::new($false))
        Move-Item -LiteralPath $temporary -Destination $result -Force
        return
    }
    if($component -in @('WinRAR','Yandex')){
        Remove-Item -LiteralPath $request.FullName -Force
        $softwareRoot=Join-Path (Split-Path -Parent $PSScriptRoot) 'Tools\Software'
        $package=if($component -eq 'WinRAR'){'winrar-x64-723ru.exe'}else{'Yandex.exe'}
        $expected=if($component -eq 'WinRAR'){'831EE7523E1D9D542DDA1531E88D9F229312F2392CD7A99F9B9C885AB70604F2'}else{'E761361E28510C954A7F60A49E625EE1ECCC318CA1D0F05D7255A05E3E7DF4AF'}
        $installer=Join-Path $softwareRoot $package
        if(!(Test-Path -LiteralPath $installer -PathType Leaf)){throw "Missing bundled installer: $package"}
        if((Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash -ne $expected){throw "Bundled installer hash mismatch: $package"}
        if((Get-AuthenticodeSignature -LiteralPath $installer).Status -ne 'Valid'){throw "Bundled installer signature invalid: $package"}
        $arguments=if($component -eq 'WinRAR'){@('/S')}else{@('--silent','--system-level','--do-not-launch-browser')}
        $process=Start-Process -FilePath $installer -ArgumentList $arguments -WorkingDirectory $softwareRoot -WindowStyle Hidden -PassThru -Wait -ErrorAction Stop
        if($process.ExitCode -notin @(0,3010)){throw "$component installer exited with code $($process.ExitCode)."}
        $relative=if($component -eq 'WinRAR'){'WinRAR\WinRAR.exe'}else{'Yandex\YandexBrowser\Application\browser.exe'}
        $deadline=[DateTime]::UtcNow.AddMinutes(2)
        do {
            $installed=@($env:ProgramFiles,${env:ProgramFiles(x86)}) | Where-Object { $_ } |
                ForEach-Object { Join-Path $_ $relative } | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf }
            if($installed){break}
            Start-Sleep -Seconds 2
        } while([DateTime]::UtcNow -lt $deadline)
        if(!$installed){throw "$component installer exited successfully, but no machine-wide installation was found. Check installer log and network access."}
        [IO.File]::WriteAllText($temporary,'OK',[Text.UTF8Encoding]::new($false))
        Move-Item -LiteralPath $temporary -Destination $result -Force
        return
    }
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
    $detail=if(Test-Path -LiteralPath $resultFromHelper){[IO.File]::ReadAllText($resultFromHelper,[Text.Encoding]::UTF8).Trim()}else{"Installer helper exited with code $($process.ExitCode) without a result."}
    if($process.ExitCode -eq 0){
        [IO.File]::WriteAllText((Join-Path $run 'details.txt'),$detail,[Text.UTF8Encoding]::new($false))
        $detail='OK'
    }
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
