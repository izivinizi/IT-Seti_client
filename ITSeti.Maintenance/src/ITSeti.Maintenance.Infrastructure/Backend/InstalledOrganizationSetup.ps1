$ErrorActionPreference='Stop'
$base=Join-Path $env:ProgramData 'ITSeti\Maintenance'
$requestRoot=Join-Path $base 'OrganizationSetupRequests'
$resultRoot=Join-Path $base 'OrganizationSetupRuns'
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
    if (Test-Path -LiteralPath (Join-Path $base 'server-device.json')) {
        Remove-Item -LiteralPath $request.FullName -Force
        $serverWorker = Join-Path $PSScriptRoot 'ServerSoftware.ps1'
        if (!(Test-Path -LiteralPath $serverWorker -PathType Leaf)) { throw 'Trusted server software worker is missing.' }
        $powershell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
        $arguments = '-NoProfile -ExecutionPolicy Bypass -File "'+$serverWorker+'"'
        if ($component) { $arguments += ' -Component "'+$component.Trim()+'"' }
        $process = Start-Process -FilePath $powershell -ArgumentList $arguments -WindowStyle Hidden -PassThru -Wait
        if ($process.ExitCode -ne 0) {
            $errorFile = Join-Path $base 'server-software-error.txt'
            $detail = if (Test-Path -LiteralPath $errorFile) { [IO.File]::ReadAllText($errorFile,[Text.Encoding]::UTF8).Trim() } else { '' }
            if (!$detail) { $detail = "Системный worker завершился с кодом $($process.ExitCode)." }
            throw "Установка ПО с сервера не выполнена: $detail"
        }
        [IO.File]::WriteAllText($temporary,'OK',[Text.UTF8Encoding]::new($false))
        Move-Item -LiteralPath $temporary -Destination $result -Force
        return
    }
    throw 'Для установки управляемого ПО подключите ПК к серверу ИТ-Сети. Установочные файлы загружаются из серверного каталога.'
} catch {
    [IO.File]::WriteAllText($temporary,$_.Exception.ToString(),[Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporary -Destination $result -Force
    if($stagedRoot -and $stagedRoot.StartsWith($run,[StringComparison]::OrdinalIgnoreCase)){
        Remove-Item -LiteralPath $stagedRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
    exit 1
}
