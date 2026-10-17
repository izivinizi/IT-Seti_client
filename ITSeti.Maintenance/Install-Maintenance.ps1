param([string]$PackageRoot=$PSScriptRoot,[string]$InstallRoot=(Join-Path $env:ProgramFiles 'ITSeti Maintenance'),[switch]$PreinstalledApp,[string]$ResultFile='',[switch]$RepairPermissions)
$ErrorActionPreference='Stop'
$installStage='Инициализация'
$data=Join-Path $env:ProgramData 'ITSeti\Maintenance'
$installLog=Join-Path $data 'install.log'
$stageTimer=[Diagnostics.Stopwatch]::StartNew()
function Write-InstallLog([string]$Message) {
    try {
        New-Item -ItemType Directory -Path $data -Force | Out-Null
        Add-Content -LiteralPath $installLog -Value ('[{0:yyyy-MM-dd HH:mm:ss}] {1}' -f (Get-Date),$Message) -Encoding UTF8
    } catch { }
}
function Set-InstallStage([string]$Stage,[string]$Code='CONFIG') {
    Write-InstallLog ('Этап «{0}»: {1:N1} с' -f $script:installStage,$script:stageTimer.Elapsed.TotalSeconds)
    $script:installStage=$Stage
    $script:stageTimer.Restart()
    Write-InstallLog ('Начат этап: '+$Stage)
    Write-Output ('ITSETI_STAGE|'+$Code)
}
function Set-InstallAcl([string]$Path,[string[]]$Arguments) {
    if(!(Test-Path -LiteralPath $Path)){throw "Каталог или файл для настройки доступа не найден: $Path"}
    $aclOutput=@(& icacls.exe $Path @Arguments 2>&1)
    $aclExitCode=$LASTEXITCODE
    if($aclExitCode -ne 0){
        throw "Не удалось настроить доступ к $Path (код $aclExitCode): $($aclOutput -join ' ')"
    }
}
function Grant-SystemTreeFullControl([string]$Path) {
    $aclOutput=@(& icacls.exe $Path /grant '*S-1-5-18:(OI)(CI)F' /T /C 2>&1 | Select-Object -Last 8)
    $aclExitCode=$LASTEXITCODE
    if($aclExitCode -ne 0){
        $diagnostic=($aclOutput | Select-Object -Last 8) -join ' '
        throw "Не удалось восстановить доступ службы SYSTEM к существующим данным $Path (код $aclExitCode): $diagnostic"
    }
    $summary=($aclOutput | Select-Object -Last 2) -join ' '
    Write-InstallLog "Права SYSTEM на файлах обслуживания проверены и восстановлены: $summary"
}
trap {
    $detail='Этап: '+$installStage+[Environment]::NewLine+$_.Exception.Message
    $technical=$_.Exception.ToString()+[Environment]::NewLine+$_.ScriptStackTrace
    Write-InstallLog ('ОШИБКА '+$detail+[Environment]::NewLine+$technical)
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
Remove-Item -LiteralPath (Join-Path $data 'installed.flag'),(Join-Path $data 'install-status.txt') -Force -ErrorAction SilentlyContinue
Set-InstallStage 'Проверка файлов приложения и инструментов' 'FILES'
$sourceApp=if($PreinstalledApp){$InstallRoot}else{Join-Path $PackageRoot 'App'}
$sourceTools=if($PreinstalledApp){Join-Path $InstallRoot 'Tools'}else{Join-Path $PackageRoot 'Tools'}
$sourceService=if($PreinstalledApp){Join-Path $InstallRoot 'Service'}else{Join-Path $PackageRoot 'Service'}
if(!(Test-Path -LiteralPath (Join-Path $sourceApp 'ITSeti.Maintenance.exe') -PathType Leaf)){throw 'Published application is missing.'}
foreach($relative in @('CrystalDiskInfo9_6_3_Portable\DiskInfo64.exe','CrystalDiskMark9\CdmResource\DiskSpd\DiskSpd64.exe','PawnIO\PawnIO_setup.exe')){
    if(!(Test-Path -LiteralPath (Join-Path $sourceTools $relative) -PathType Leaf)){throw "Tool is missing: $relative"}
}
foreach($relative in @('Panel\DesktopInfo.ini','Panel\update-support-ids.ps1','Panel\start-panel.vbs')){
    if(!(Test-Path -LiteralPath (Join-Path $sourceApp $relative) -PathType Leaf)){throw "Файл панели отсутствует: $relative"}
}
$serviceExe=Join-Path $InstallRoot 'Service\ITSeti.Maintenance.Service.exe'
if(!(Test-Path -LiteralPath (Join-Path $sourceService 'ITSeti.Maintenance.Service.exe') -PathType Leaf)){throw 'Maintenance service executable is missing.'}
$oldNotifier=Get-Process -Name 'ITSeti.Maintenance.TicketNotifier' -ErrorAction SilentlyContinue
if($oldNotifier){$oldNotifier | Stop-Process -Force -ErrorAction SilentlyContinue; Start-Sleep -Milliseconds 500}
$runKey='HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run'
Remove-ItemProperty -Path $runKey -Name 'ITSeti-Maintenance-Tickets' -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path $InstallRoot 'Notifier') -Recurse -Force -ErrorAction SilentlyContinue
$install=$InstallRoot
$installStage='Создание каталогов и копирование файлов'
New-Item -ItemType Directory -Path $install,$data,(Join-Path $data 'Runs'),(Join-Path $data 'Repairs'),(Join-Path $data 'Logs'),(Join-Path $data 'CleanupRequests'),(Join-Path $data 'CleanupRuns'),(Join-Path $data 'MonitoringQueue'),(Join-Path $data 'WindowsUpdate'),(Join-Path $data 'OrganizationSetupRequests'),(Join-Path $data 'OrganizationSetupRuns') -Force | Out-Null
Remove-Item -LiteralPath (Join-Path $install 'Setup') -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path (Join-Path $install 'Backend') -Force | Out-Null
if(!$PreinstalledApp){Get-ChildItem -LiteralPath $sourceApp -Force | Copy-Item -Destination $install -Recurse -Force}
if(!$PreinstalledApp){
    $serviceTarget=Join-Path $install 'Service'
    New-Item -ItemType Directory -Path $serviceTarget -Force | Out-Null
    Get-ChildItem -LiteralPath $sourceService -Force | Copy-Item -Destination $serviceTarget -Recurse -Force
}
if(!$PreinstalledApp){
}
if(!$PreinstalledApp){
    foreach($folder in @('CrystalDiskInfo9_6_3_Portable','CrystalDiskMark9','TreeSize Free','PawnIO','HWiNFO')){
        $source=Join-Path $sourceTools $folder
        if(Test-Path -LiteralPath $source -PathType Container){
            $target=Join-Path (Join-Path $install 'Tools') $folder
            New-Item -ItemType Directory -Path $target -Force | Out-Null
            Get-ChildItem -LiteralPath $source -Force | Copy-Item -Destination $target -Recurse -Force
        }
    }
}
Remove-Item -LiteralPath (Join-Path $install 'Backend\Install-OrganizationSoftware.ps1') -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path $install 'Tools\Software') -Recurse -Force -ErrorAction SilentlyContinue
$pawnIoInstaller=Join-Path $sourceTools 'PawnIO\PawnIO_setup.exe'
$pawnIoLog=Join-Path $data 'cpu-sensor-driver.txt'
$pawnIoHash='1F519A22E47187F70A1379A48CA604981C4FCF694F4E65B734AAA74A9FBA3032'
Set-InstallStage 'Проверка и установка драйвера датчика' 'SENSOR'
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
            $pawnIoProcess=Start-Process -FilePath $pawnIoInstaller -ArgumentList @('-install','-silent') -PassThru -WindowStyle Hidden
            if(!$pawnIoProcess.WaitForExit(60000)){
                try {$pawnIoProcess.Kill()} catch {}
                $pawnIoMessage='Установка PawnIO превысила 60 секунд и была остановлена; приложение установлено, но датчик температуры может быть недоступен.'
            }else{
                $installedVersion=(Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO' -ErrorAction SilentlyContinue).DisplayVersion
                if($installedVersion){$pawnIoMessage='PawnIO '+$installedVersion+' установлен.'}
                elseif($pawnIoProcess.ExitCode -eq 3010){$pawnIoMessage='PawnIO установлен; требуется перезагрузка Windows.'}
                else{$pawnIoMessage='PawnIO не установлен (код '+$pawnIoProcess.ExitCode+'); температура CPU останется недоступна.'}
            }
            [IO.File]::WriteAllText($pawnIoLog,$pawnIoMessage,[Text.Encoding]::UTF8)
        }
    }
} catch {
    [IO.File]::WriteAllText($pawnIoLog,('PawnIO не установлен; установка приложения продолжена. '+$_.Exception.ToString()),[Text.Encoding]::UTF8)
}
$securityRoot=Join-Path $env:ProgramData 'ITSeti'
Set-InstallStage 'Настройка доступа к данным обслуживания' 'ACL'
Set-InstallAcl $securityRoot @('/inheritance:r','/grant:r','*S-1-5-18:(OI)(CI)F','*S-1-5-32-544:(OI)(CI)F','*S-1-5-32-545:(OI)(CI)RX')
# The root ACL is inherited by new files, but older files may carry protected
# ACLs from previous installers or manual repairs. The SYSTEM service must be
# able to atomically replace its cache and status files, so restore only its
# full-control ACE recursively while preserving every other existing ACE.
$aclMarker=Join-Path $data 'system-acl-v1.ok'
if($RepairPermissions -or !(Test-Path -LiteralPath $aclMarker -PathType Leaf)){
    Grant-SystemTreeFullControl $securityRoot
    [IO.File]::WriteAllText($aclMarker,'system-full-control-v1',[Text.Encoding]::ASCII)
} else {Write-InstallLog 'Миграция прав SYSTEM уже выполнена; повторный обход архива проверок пропущен.'}
$enrollmentFile=Join-Path $data 'server-device.json'
if(Test-Path -LiteralPath $enrollmentFile -PathType Leaf){
    Set-InstallAcl $enrollmentFile @('/inheritance:r','/grant:r','*S-1-5-18:F','*S-1-5-32-544:F','*S-1-5-32-545:R')
}
$cleanupQueue=Join-Path $data 'CleanupRequests'
Set-InstallAcl $cleanupQueue @('/grant:r','*S-1-5-32-545:(OI)(CI)M')
$applicationLogs=Join-Path $data 'Logs'
Set-InstallAcl $applicationLogs @('/inheritance:r','/grant:r','*S-1-5-18:(OI)(CI)F','*S-1-5-32-544:(OI)(CI)F','*S-1-5-32-545:(OI)(CI)M')
$cleanupRuns=Join-Path $data 'CleanupRuns'
Set-InstallAcl $cleanupRuns @('/inheritance:r','/grant:r','*S-1-5-18:(OI)(CI)F','*S-1-5-32-544:(OI)(CI)F','*S-1-5-32-545:(OI)(CI)RX')
$organizationRequests=Join-Path $data 'OrganizationSetupRequests'
Set-InstallAcl $organizationRequests @('/grant:r','*S-1-5-32-545:(OI)(CI)M')
$organizationRuns=Join-Path $data 'OrganizationSetupRuns'
Set-InstallAcl $organizationRuns @('/inheritance:r','/grant:r','*S-1-5-18:(OI)(CI)F','*S-1-5-32-544:(OI)(CI)F','*S-1-5-32-545:(OI)(CI)RX')
$inventory=Join-Path $data 'inventory.txt'
if(!(Test-Path -LiteralPath $inventory -PathType Leaf)){[IO.File]::WriteAllText($inventory,'',[Text.Encoding]::ASCII)}
& icacls.exe $inventory /grant '*S-1-5-32-545:M' | Out-Null
if($LASTEXITCODE -ne 0){throw 'Could not configure inventory number access.'}
. (Join-Path $install 'Backend\ScheduledFull.ps1')
[void](Initialize-ScheduledFullBaseline $data)
$passwordPolicy=Join-Path $install 'Backend\Set-LocalPasswordsNeverExpire.ps1'
if(Test-Path -LiteralPath $passwordPolicy){try{& $passwordPolicy -LogPath (Join-Path $data 'local-password-policy.log')}catch{Write-InstallLog ('Local password expiry policy could not be updated: '+$_.Exception.GetType().Name)}}
Set-InstallStage 'Регистрация системных задач' 'TASKS'
$taskName='ITSeti-Maintenance-Full'
$uploadTaskName='ITSeti-Maintenance-Upload'
$catalogTaskName='ITSeti-Maintenance-Catalog'
$reportQueue=Join-Path $data 'ReportQueue'
New-Item -ItemType Directory -Path $reportQueue -Force | Out-Null
& icacls.exe $reportQueue /grant '*S-1-5-32-545:(OI)(CI)M' | Out-Null
if($LASTEXITCODE -ne 0){throw 'Не удалось разрешить очередь отчётов пользователям.'}
$worker=Join-Path $install 'Backend\InstalledCheck.ps1'
try {
    $taskTrigger=New-ScheduledTaskTrigger -Once -At (Get-Date).AddYears(20)
    $autoFullTrigger=New-ScheduledTaskTrigger -Daily -At ([DateTime]::Today.AddHours(4))
    $uploadTrigger=New-ScheduledTaskTrigger -Daily -At ([DateTime]::Today.AddHours(5.5))
    $taskSettings=New-ScheduledTaskSettingsSet -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Hours 3) -MultipleInstances IgnoreNew
    $autoFullSettings=New-ScheduledTaskSettingsSet -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Hours 6) -MultipleInstances IgnoreNew
    $updateTaskSettings=New-ScheduledTaskSettingsSet -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Minutes 30) -MultipleInstances IgnoreNew
    $taskPrincipal=New-ScheduledTaskPrincipal -UserId 'S-1-5-18' -LogonType ServiceAccount -RunLevel Highest
    $powershell=Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    if(!(Test-Path -LiteralPath $powershell -PathType Leaf)){throw "PowerShell не найден: $powershell"}
    $schedulerService=Get-Service -Name Schedule -ErrorAction Stop
    if($schedulerService.Status -ne 'Running'){
        try {Start-Service -Name Schedule -ErrorAction Stop}
        catch {throw "Служба планировщика Windows не запущена: $($_.Exception.Message)"}
    }
    Write-InstallLog ('Планировщик Windows: '+(Get-Service -Name Schedule -ErrorAction Stop).Status)
    $scheduler=New-Object -ComObject Schedule.Service
    $scheduler.Connect()
    foreach($obsoleteQuickTask in @('ITSeti-Maintenance-QuickFull','ITSeti-Maintenance-AutoQuick')){
        $oldTask=Get-ScheduledTask -TaskName $obsoleteQuickTask -ErrorAction SilentlyContinue
        if($oldTask){Unregister-ScheduledTask -TaskName $obsoleteQuickTask -Confirm:$false -ErrorAction Stop;Write-InstallLog "Удалена устаревшая задача быстрой проверки $obsoleteQuickTask."}
    }
    foreach($name in @($taskName,'ITSeti-Maintenance-AutoFullRepair','ITSeti-Maintenance-Repair','ITSeti-Maintenance-Cleanup','ITSeti-Maintenance-OrganizationSetup','ITSeti-Maintenance-DisableUpdates','ITSeti-Maintenance-RestoreUpdates','ITSeti-Maintenance-Update','ITSeti-Maintenance-ServiceHealth',$uploadTaskName,$catalogTaskName)){
        $installStage="Регистрация задачи $name"
        $script=if($name -eq $uploadTaskName){Join-Path $install 'Backend\Upload-Reports.ps1'}elseif($name -eq 'ITSeti-Maintenance-Repair'){Join-Path $install 'Backend\InstalledRepair.ps1'}elseif($name -eq 'ITSeti-Maintenance-Cleanup'){Join-Path $install 'Backend\InstalledCleanup.ps1'}elseif($name -eq 'ITSeti-Maintenance-OrganizationSetup'){Join-Path $install 'Backend\InstalledOrganizationSetup.ps1'}elseif($name -eq 'ITSeti-Maintenance-Update'){Join-Path $install 'Backend\Update-Application.ps1'}elseif($name -match 'Updates$'){Join-Path $install 'Backend\Set-WindowsAutomaticUpdates.ps1'}else{$worker}
        $arguments='-NoProfile -ExecutionPolicy Bypass -File "'+$script+'"'
        if($name -eq $catalogTaskName){
            $script=Join-Path $install 'Backend\ServerSoftware.ps1'
            $arguments='-NoProfile -ExecutionPolicy Bypass -File "'+$script+'" -CatalogOnly'
        }
        if($name -eq 'ITSeti-Maintenance-AutoFullRepair'){$arguments+=' -StartRepair'}
        if($name -eq 'ITSeti-Maintenance-ServiceHealth'){
            $script=Join-Path $install 'Backend\ServiceHealth.ps1'
            $arguments='-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "'+$script+'"'
        }
        if($name -eq 'ITSeti-Maintenance-DisableUpdates'){$arguments+=' -Action Disable'}
        if($name -eq 'ITSeti-Maintenance-RestoreUpdates'){$arguments+=' -Action Restore'}
        if(!(Test-Path -LiteralPath $script -PathType Leaf)){throw "Task script is missing: $script"}
        $taskAction=New-ScheduledTaskAction -Execute $powershell -Argument $arguments -WorkingDirectory $install
        $trigger=if($name -eq $uploadTaskName){$uploadTrigger}elseif($name -eq 'ITSeti-Maintenance-AutoFullRepair'){$autoFullTrigger}else{$taskTrigger}
        if($name -eq 'ITSeti-Maintenance-ServiceHealth'){
            $trigger=@((New-ScheduledTaskTrigger -AtStartup),(New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(15) -RepetitionInterval (New-TimeSpan -Minutes 15)))
        }
        $settings=if($name -eq 'ITSeti-Maintenance-AutoFullRepair'){$autoFullSettings}elseif($name -eq 'ITSeti-Maintenance-Update'){$updateTaskSettings}else{$taskSettings}
        try {
            $lastTaskError=$null
            for($attempt=1;$attempt -le 3;$attempt++){
                try {
                    Register-ScheduledTask -TaskName $name -Action $taskAction -Trigger $trigger -Settings $settings -Principal $taskPrincipal -Force -ErrorAction Stop | Out-Null
                    $lastTaskError=$null
                    break
                } catch {
                    $lastTaskError=$_.Exception
                    Write-InstallLog ("Попытка ${attempt}/3 для задачи ${name}: "+$lastTaskError.Message)
                    if($attempt -lt 3){Start-Sleep -Seconds $attempt}
                }
            }
            if($lastTaskError){throw $lastTaskError}
            $task=$null
            for($attempt=1;$attempt -le 10;$attempt++){
                try {$task=$scheduler.GetFolder('\').GetTask($name);break}
                catch {
                    if($attempt -eq 10){throw}
                    Start-Sleep -Milliseconds 500
                }
            }
            if(!$task){throw "Task Scheduler did not expose registered task $name."}
            $engineerTask=$name -in @('ITSeti-Maintenance-AutoFullRepair','ITSeti-Maintenance-Repair',
                'ITSeti-Maintenance-OrganizationSetup','ITSeti-Maintenance-DisableUpdates','ITSeti-Maintenance-RestoreUpdates','ITSeti-Maintenance-ServiceHealth')
            $task.SetSecurityDescriptor($(if($engineerTask){'D:P(A;;FA;;;SY)(A;;FA;;;BA)'}
                else{'D:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;GRGX;;;BU)'}),0)
            $definition=$task.Definition
            $registeredSid=if($definition.Principal.UserId -eq 'S-1-5-18'){'S-1-5-18'}else{([Security.Principal.NTAccount]$definition.Principal.UserId).Translate([Security.Principal.SecurityIdentifier]).Value}
            if($registeredSid -ne 'S-1-5-18' -or $definition.Principal.LogonType -ne 5 -or $definition.Principal.RunLevel -ne 1){
                throw "Task $name has an invalid execution identity."
            }
            if($definition.Actions.Count -ne 1 -or $definition.Actions.Item(1).Path -ne $taskAction.Execute -or $definition.Actions.Item(1).Arguments -ne $taskAction.Arguments){
                throw "Task $name has an invalid command."
            }
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
Set-InstallStage 'Контрольный запуск планового задания SYSTEM' 'VERIFY'
$scheduleProbe=[guid]::NewGuid().ToString('N')
$scheduleRequest=Join-Path $data 'schedule-self-test.request'
$scheduleResult=Join-Path $data 'schedule-self-test.result'
try {
    [IO.File]::WriteAllText($scheduleRequest,$scheduleProbe,[Text.Encoding]::ASCII)
    Start-ScheduledTask -TaskName 'ITSeti-Maintenance-AutoFullRepair' -ErrorAction Stop
    $probePassed=$false
    for($attempt=0;$attempt -lt 120;$attempt++){
        if((Test-Path -LiteralPath $scheduleResult) -and [IO.File]::ReadAllText($scheduleResult).Trim() -eq $scheduleProbe){$probePassed=$true;break}
        Start-Sleep -Milliseconds 500
    }
    if(!$probePassed){throw 'Плановое задание не прошло контрольный запуск SYSTEM за 60 секунд.'}
    Write-InstallLog 'Контрольный запуск плановой задачи SYSTEM выполнен успешно.'
} finally {Remove-Item -LiteralPath $scheduleRequest -Force -ErrorAction SilentlyContinue}
Set-InstallStage 'Регистрация системной службы' 'SERVICE'
$serviceName='ITSetiMaintenanceService'
$legacyTemperatureTask=Get-ScheduledTask -TaskName 'ITSeti-Maintenance-Temperature' -ErrorAction SilentlyContinue
if($legacyTemperatureTask){
    Unregister-ScheduledTask -TaskName 'ITSeti-Maintenance-Temperature' -Confirm:$false -ErrorAction SilentlyContinue
    Write-InstallLog 'Удалена устаревшая отдельная задача чтения температуры.'
}
$legacyPayloads=@(
    (Join-Path $install 'Backend\CpuLoad.ps1'),
    (Join-Path $install 'CpuLoad.ps1'),
    (Join-Path $install 'LibreHardwareMonitorLib.dll'),
    (Join-Path $install 'OpenHardwareMonitorLib.dll'),
    (Join-Path $install 'Tools\LibreHardwareMonitor')
)
foreach($legacyPayload in $legacyPayloads){
    Remove-Item -LiteralPath $legacyPayload -Recurse -Force -ErrorAction SilentlyContinue
}
$existingService=Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if($existingService){
    if($existingService.Status -ne 'Stopped'){
        Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
        for($attempt=0;$attempt -lt 80;$attempt++){
            $state=(Get-Service -Name $serviceName -ErrorAction SilentlyContinue).Status
            if($state -eq 'Stopped'){break}
            Start-Sleep -Milliseconds 250
        }
        if((Get-Service -Name $serviceName -ErrorAction Stop).Status -ne 'Stopped'){throw 'Служба не остановилась перед обновлением.'}
    }
    & sc.exe config $serviceName binPath= ('"'+$serviceExe+'"') start= auto obj= LocalSystem DisplayName= 'ИТ-Сети: обслуживание ПК' | Out-Null
    if($LASTEXITCODE -ne 0){throw 'Не удалось обновить регистрацию службы.'}
}else{
    New-Service -Name $serviceName -BinaryPathName ('"'+$serviceExe+'"') -DisplayName 'ИТ-Сети: обслуживание ПК' -Description 'Диагностика, мониторинг и системные операции ИТ-Сети.' -StartupType Automatic -ErrorAction Stop | Out-Null
}
& sc.exe config $serviceName start= auto | Out-Null
if($LASTEXITCODE -ne 0){throw 'Не удалось включить автоматический запуск службы.'}
& sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null
if($LASTEXITCODE -ne 0){throw 'Не удалось настроить восстановление службы.'}
& sc.exe failureflag $serviceName 1 | Out-Null
if($LASTEXITCODE -ne 0){throw 'Не удалось включить восстановление при аварийном завершении службы.'}
& sc.exe description $serviceName 'Диагностика, мониторинг и системные операции ИТ-Сети.' | Out-Null
Start-Service -Name $serviceName -ErrorAction Stop
for($attempt=0;$attempt -lt 120;$attempt++){
    $registeredService=Get-Service -Name $serviceName -ErrorAction Stop
    if($registeredService.Status -eq 'Running'){break}
    Start-Sleep -Milliseconds 250
}
if((Get-Service -Name $serviceName -ErrorAction Stop).Status -ne 'Running'){throw 'Служба не перешла в состояние Running.'}
$serviceCim=Get-CimInstance Win32_Service -Filter ("Name='{0}'" -f $serviceName) -ErrorAction Stop
if($serviceCim.StartName -ne 'LocalSystem' -or $serviceCim.StartMode -notin @('Auto','Automatic') -or [bool]$serviceCim.DelayedAutoStart){throw 'Служба должна запускаться автоматически от LocalSystem без отложенного старта.'}
if([string]$serviceCim.PathName -notmatch [regex]::Escape($serviceExe)){throw 'Служба указывает на другой исполняемый файл.'}
Write-InstallLog ('Служба '+$serviceName+' зарегистрирована и запущена.')
Set-InstallStage 'Завершение установки' 'FINISH'
$legacyTask=Get-ScheduledTask -TaskName 'ITSeti-Maintenance-FullRepair' -ErrorAction SilentlyContinue
if($legacyTask){Unregister-ScheduledTask -TaskName 'ITSeti-Maintenance-FullRepair' -Confirm:$false}
$runKey='HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run'
Remove-ItemProperty -Path $runKey -Name 'ITSeti-Maintenance-Quick' -ErrorAction SilentlyContinue
New-ItemProperty -Path $runKey -Name 'ITSeti-Maintenance-Status' -Value ('"'+(Join-Path $install 'ITSeti.Maintenance.exe')+'" --scheduled-quick') -PropertyType String -Force | Out-Null
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
[IO.File]::WriteAllText((Join-Path $data 'install-status.txt'),'OK: системная служба и резервные задачи зарегистрированы.',[Text.UTF8Encoding]::new($false))
if(!(Test-Path -LiteralPath (Join-Path $data 'installed.flag') -PathType Leaf)){throw 'Installation marker was not written.'}
Write-Host "Installed to $install. Users may launch the application without elevation."
