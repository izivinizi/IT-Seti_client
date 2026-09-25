param([string]$PackageRoot=$PSScriptRoot,[string]$InstallRoot=(Join-Path $env:ProgramFiles 'ITSeti Maintenance'),[switch]$PreinstalledApp,[string]$ResultFile='')
$ErrorActionPreference='Stop'
$installStage='Инициализация'
$data=Join-Path $env:ProgramData 'ITSeti\Maintenance'
$installLog=Join-Path $data 'install.log'
function Write-InstallLog([string]$Message) {
    try {
        New-Item -ItemType Directory -Path $data -Force | Out-Null
        Add-Content -LiteralPath $installLog -Value ('[{0:yyyy-MM-dd HH:mm:ss}] {1}' -f (Get-Date),$Message) -Encoding UTF8
    } catch { }
}
trap {
    $detail='Этап: '+$installStage+[Environment]::NewLine+$_.Exception.ToString()+[Environment]::NewLine+$_.ScriptStackTrace
    Write-InstallLog ('ОШИБКА '+$detail)
    if($ResultFile){
        try {
            $resultDirectory=Split-Path -Parent $ResultFile
            if($resultDirectory){New-Item -ItemType Directory -Path $resultDirectory -Force | Out-Null}
            [IO.File]::WriteAllText($ResultFile,$detail,[Text.Encoding]::Default)
        } catch { Write-InstallLog ('Не удалось записать файл результата: '+$_.Exception.ToString()) }
    }
    exit 1
}
$installStage='Проверка прав администратора'
$admin=([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if(!$admin){throw 'Run this installer once as administrator.'}
$installStage='Проверка файлов приложения и инструментов'
$sourceApp=if($PreinstalledApp){$InstallRoot}else{Join-Path $PackageRoot 'App'}
$sourceTools=if($PreinstalledApp){Join-Path $InstallRoot 'Tools'}else{Join-Path $PackageRoot 'Tools'}
if(!(Test-Path -LiteralPath (Join-Path $sourceApp 'ITSeti.Maintenance.exe') -PathType Leaf)){throw 'Published application is missing.'}
foreach($relative in @('CrystalDiskInfo9_6_3_Portable\DiskInfo64.exe','CrystalDiskMark9\CdmResource\DiskSpd\DiskSpd64.exe','PawnIO\PawnIO_setup.exe')){
    if(!(Test-Path -LiteralPath (Join-Path $sourceTools $relative) -PathType Leaf)){throw "Tool is missing: $relative"}
}
$install=$InstallRoot
$installStage='Создание каталогов и копирование файлов'
New-Item -ItemType Directory -Path $install,$data,(Join-Path $data 'Runs'),(Join-Path $data 'Repairs'),(Join-Path $data 'CleanupRequests'),(Join-Path $data 'CleanupRuns'),(Join-Path $data 'WindowsUpdate') -Force | Out-Null
if(!$PreinstalledApp){Get-ChildItem -LiteralPath $sourceApp -Force | Copy-Item -Destination $install -Recurse -Force}
if(!$PreinstalledApp){
    foreach($folder in @('CrystalDiskInfo9_6_3_Portable','CrystalDiskMark9','TreeSize Free','PawnIO')){
        $source=Join-Path $sourceTools $folder
        if(Test-Path -LiteralPath $source -PathType Container){
            $target=Join-Path (Join-Path $install 'Tools') $folder
            New-Item -ItemType Directory -Path $target -Force | Out-Null
            Get-ChildItem -LiteralPath $source -Force | Copy-Item -Destination $target -Recurse -Force
        }
    }
}
$pawnIoInstaller=Join-Path $sourceTools 'PawnIO\PawnIO_setup.exe'
$pawnIoLog=Join-Path $data 'cpu-sensor-driver.txt'
$pawnIoHash='1F519A22E47187F70A1379A48CA604981C4FCF694F4E65B734AAA74A9FBA3032'
$installStage='Проверка и установка драйвера датчика'
try {
    $pawnIoSignature=Get-AuthenticodeSignature -LiteralPath $pawnIoInstaller
    $pawnIoSigner=$pawnIoSignature.SignerCertificate
    $actualHash=(Get-FileHash -LiteralPath $pawnIoInstaller -Algorithm SHA256).Hash
    if($actualHash -ne $pawnIoHash -or $pawnIoSignature.Status -ne 'Valid' -or !$pawnIoSigner -or $pawnIoSigner.Thumbprint -ne 'F380DCC9F706E2756A5047B832FFE719E1BC35F5'){
        [IO.File]::WriteAllText($pawnIoLog,'PawnIO не установлен: не прошла проверка хеша или цифровой подписи.',[Text.Encoding]::UTF8)
    } else {
        $pawnIoUninstall=Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO' -ErrorAction SilentlyContinue
        if($pawnIoUninstall.DisplayVersion){
            [IO.File]::WriteAllText($pawnIoLog,('PawnIO уже установлен, версия '+$pawnIoUninstall.DisplayVersion+'; существующий драйвер оставлен без изменений.'),[Text.Encoding]::UTF8)
        } else {
            $pawnIoProcess=Start-Process -FilePath $pawnIoInstaller -ArgumentList @('-install','-silent') -Wait -PassThru -WindowStyle Hidden
            $installedVersion=(Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO' -ErrorAction SilentlyContinue).DisplayVersion
            if($installedVersion){$pawnIoMessage='PawnIO '+$installedVersion+' установлен.'}
            elseif($pawnIoProcess.ExitCode -eq 3010){$pawnIoMessage='PawnIO установлен; требуется перезагрузка Windows.'}
            else{$pawnIoMessage='PawnIO не установлен (код '+$pawnIoProcess.ExitCode+'); температура CPU останется недоступна.'}
            [IO.File]::WriteAllText($pawnIoLog,$pawnIoMessage,[Text.Encoding]::UTF8)
        }
    }
} catch {
    [IO.File]::WriteAllText($pawnIoLog,('PawnIO не установлен; установка приложения продолжена. '+$_.Exception.ToString()),[Text.Encoding]::UTF8)
}
$securityRoot=Join-Path $env:ProgramData 'ITSeti'
$installStage='Настройка доступа к данным обслуживания'
& icacls.exe $securityRoot /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-32-545:(OI)(CI)RX' | Out-Null
if($LASTEXITCODE -ne 0){throw 'Could not secure ProgramData root.'}
$cleanupQueue=Join-Path $data 'CleanupRequests'
& icacls.exe $cleanupQueue /grant:r '*S-1-5-32-545:(OI)(CI)M' | Out-Null
if($LASTEXITCODE -ne 0){throw 'Could not allow cleanup requests.'}
$cleanupRuns=Join-Path $data 'CleanupRuns'
& icacls.exe $cleanupRuns /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-32-545:(OI)(CI)RX' | Out-Null
if($LASTEXITCODE -ne 0){throw 'Could not secure cleanup results.'}
$inventory=Join-Path $data 'inventory.txt'
if(!(Test-Path -LiteralPath $inventory -PathType Leaf)){[IO.File]::WriteAllText($inventory,'',[Text.Encoding]::ASCII)}
& icacls.exe $inventory /grant '*S-1-5-32-545:M' | Out-Null
if($LASTEXITCODE -ne 0){throw 'Could not configure inventory number access.'}
$installStage='Регистрация системных задач'
$taskName='ITSeti-Maintenance-Full'
$temperatureTaskName='ITSeti-Maintenance-Temperature'
$worker=Join-Path $install 'Backend\InstalledCheck.ps1'
try {
    $taskTrigger=New-ScheduledTaskTrigger -Once -At (Get-Date).AddYears(20)
    $quickTrigger=New-ScheduledTaskTrigger -Once -At (Get-Date).AddYears(20)
    $autoFullTrigger=New-ScheduledTaskTrigger -Daily -DaysInterval 60 -At ([DateTime]::Today.AddHours(4))
    $taskSettings=New-ScheduledTaskSettingsSet -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Hours 2)
    $updateTaskSettings=New-ScheduledTaskSettingsSet -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Minutes 15) -MultipleInstances IgnoreNew
    $temperatureTaskSettings=New-ScheduledTaskSettingsSet -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Minutes 2) -MultipleInstances IgnoreNew
    $scheduler=New-Object -ComObject Schedule.Service
    $scheduler.Connect()
    foreach($name in @($taskName,'ITSeti-Maintenance-QuickFull','ITSeti-Maintenance-AutoFullRepair','ITSeti-Maintenance-Repair','ITSeti-Maintenance-Cleanup','ITSeti-Maintenance-DisableUpdates','ITSeti-Maintenance-RestoreUpdates','ITSeti-Maintenance-Update',$temperatureTaskName)){
        $installStage="Регистрация задачи $name"
        $isTemperatureTask=$name -eq $temperatureTaskName
        if($isTemperatureTask){
            $taskAction=New-ScheduledTaskAction -Execute (Join-Path $install 'ITSeti.Maintenance.exe') -Argument ('--cpu-temperature-probe "'+(Join-Path $data 'cpu-temperature.json')+'"')
        }else{
            $script=if($name -eq 'ITSeti-Maintenance-Repair'){Join-Path $install 'Backend\InstalledRepair.ps1'}elseif($name -eq 'ITSeti-Maintenance-Cleanup'){Join-Path $install 'Backend\InstalledCleanup.ps1'}elseif($name -eq 'ITSeti-Maintenance-Update'){Join-Path $install 'Backend\Update-Application.ps1'}elseif($name -match 'Updates$'){Join-Path $install 'Backend\Set-WindowsAutomaticUpdates.ps1'}else{$worker}
            $arguments='-NoProfile -ExecutionPolicy Bypass -File "'+$script+'"'
            if($name -eq 'ITSeti-Maintenance-QuickFull'){$arguments+=' -Quick'}
            if($name -eq 'ITSeti-Maintenance-AutoFullRepair'){$arguments+=' -StartRepair'}
            if($name -eq 'ITSeti-Maintenance-DisableUpdates'){$arguments+=' -Action Disable'}
            if($name -eq 'ITSeti-Maintenance-RestoreUpdates'){$arguments+=' -Action Restore'}
            $taskAction=New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $arguments
        }
        $trigger=if($name -eq 'ITSeti-Maintenance-QuickFull'){$quickTrigger}elseif($name -eq 'ITSeti-Maintenance-AutoFullRepair'){$autoFullTrigger}else{$taskTrigger}
        $settings=if($name -eq 'ITSeti-Maintenance-Update'){$updateTaskSettings}elseif($isTemperatureTask){$temperatureTaskSettings}else{$taskSettings}
        try {
            Register-ScheduledTask -TaskName $name -Action $taskAction -Trigger $trigger -Settings $settings -User 'SYSTEM' -RunLevel Highest -Force | Out-Null
            $task=$scheduler.GetFolder('\').GetTask($name)
            $task.SetSecurityDescriptor('D:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;GRGX;;;BU)',0)
            Write-InstallLog ("Задача $name зарегистрирована.")
        } catch {
            Write-InstallLog ("Не удалось зарегистрировать задачу $($name): "+$_.Exception.ToString())
            throw "Не удалось зарегистрировать обязательную задачу $name. $($_.Exception.Message)"
        }
    }
} catch {
    Write-InstallLog ('Не удалось зарегистрировать обязательные системные задачи: '+$_.Exception.ToString())
    throw
}
$installStage='Завершение установки'
[IO.File]::WriteAllText((Join-Path $data 'install-status.txt'),'OK: все обязательные системные задачи зарегистрированы.',[Text.UTF8Encoding]::new($false))
$legacyTask=Get-ScheduledTask -TaskName 'ITSeti-Maintenance-FullRepair' -ErrorAction SilentlyContinue
if($legacyTask){Unregister-ScheduledTask -TaskName 'ITSeti-Maintenance-FullRepair' -Confirm:$false}
$runKey='HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run'
New-ItemProperty -Path $runKey -Name 'ITSeti-Maintenance-Quick' -Value ('"'+(Join-Path $install 'ITSeti.Maintenance.exe')+'" --scheduled-quick') -PropertyType String -Force | Out-Null
$legacyShortcuts=@((Join-Path $env:PUBLIC 'Desktop\ITSeti Maintenance.lnk'),(Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\ITSeti Maintenance.lnk'))
if($PreinstalledApp){
    foreach($path in $legacyShortcuts){Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue}
}else{
    $shell=New-Object -ComObject WScript.Shell
    foreach($path in $legacyShortcuts){
        $shortcut=$shell.CreateShortcut($path)
        $shortcut.TargetPath=Join-Path $install 'ITSeti.Maintenance.exe'
        $shortcut.WorkingDirectory=$install
        $shortcut.IconLocation=(Join-Path $install 'ITSeti.Maintenance.exe')+',0'
        $shortcut.Save()
    }
}
[IO.File]::WriteAllText((Join-Path $data 'installed.flag'),'ITSeti-Maintenance-Full',[Text.Encoding]::ASCII)
if(!(Test-Path -LiteralPath (Join-Path $data 'installed.flag') -PathType Leaf)){throw 'Installation marker was not written.'}
Write-Host "Installed to $install. Users may launch the application without elevation."
