function Enter-MaintenanceRun([string]$Name='Global\ServiceMaintenance-AutoRun',[switch]$AllowBackgroundMaintenance) {
    $script:RunMutex=$null
    try {
        $security=New-Object Security.AccessControl.MutexSecurity
        $everyone=New-Object Security.Principal.SecurityIdentifier 'S-1-1-0'
        $security.AddAccessRule((New-Object Security.AccessControl.MutexAccessRule($everyone,'Synchronize, Modify','Allow')))
        $created=$false
        if($PSVersionTable.PSVersion.Major -ge 6){$mutex=[Threading.MutexAcl]::Create($false,$Name,[ref]$created,$security)}
        else {$mutex=New-Object Threading.Mutex($false,$Name,[ref]$created,$security)}
        $owned=$false
        try {$owned=$mutex.WaitOne(0,$false)} catch [Threading.AbandonedMutexException] {$owned=$true}
        if(!$owned){$mutex.Dispose(); throw 'Диагностика уже открыта. Используйте её окно или Results.cmd.'}
        $script:RunMutex=$mutex
        if(!$AllowBackgroundMaintenance) {
            $repair=New-Object Threading.Mutex($false,'Global\ServiceMaintenance-SystemRepair')
            try {
                $idle=$false
                try {$idle=$repair.WaitOne(0,$false)} catch [Threading.AbandonedMutexException] {$idle=$true}
                if(!$idle){throw 'Фоновое обслуживание ещё выполняется. Результаты доступны через Results.cmd.'}
                $repair.ReleaseMutex()
            } finally {$repair.Dispose()}
            if(Get-Process cleanmgr,dism,sfc,DiskSpd* -ErrorAction SilentlyContinue){throw 'Уже работает очистка, восстановление или дисковый тест. Повторный запуск пропущен.'}
        }
        return $true
    } catch {
        Exit-MaintenanceRun
        Write-Host ('Запуск отменён: '+$_.Exception.Message) -ForegroundColor Yellow
        return $false
    }
}
function Exit-MaintenanceRun {
    if($script:RunMutex){try {$script:RunMutex.ReleaseMutex()} finally {$script:RunMutex.Dispose();$script:RunMutex=$null}}
}
function New-LocalMaintenanceDirectory([string]$Kind) {
    $base = Join-Path $env:ProgramData 'ServiceMaintenance'
    if(!$script:Admin) { $base = Join-Path $env:LOCALAPPDATA 'ServiceMaintenance' }
    $path = Join-Path (Join-Path $base $Kind) ((Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $path -Force -ErrorAction Stop | Out-Null
    if($script:Admin) {
        $acl = New-Object Security.AccessControl.DirectorySecurity
        $acl.SetAccessRuleProtection($true,$false)
        foreach($sid in @('S-1-5-18','S-1-5-32-544')) {
            $identity = New-Object Security.Principal.SecurityIdentifier $sid
            $rule = New-Object Security.AccessControl.FileSystemAccessRule($identity,'FullControl','ContainerInherit, ObjectInherit','None','Allow')
            $acl.AddAccessRule($rule)
        }
        $readers = New-Object Security.Principal.SecurityIdentifier 'S-1-5-32-545'
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($readers,'ReadAndExecute','ContainerInherit, ObjectInherit','None','Allow')))
        [IO.Directory]::SetAccessControl($path,$acl)
    }
    return $path
}
function Copy-MaintenanceTool([string]$Source,[string]$Destination) {
    New-Item -ItemType Directory -Path $Destination -Force -ErrorAction Stop | Out-Null
    foreach($item in Get-ChildItem -LiteralPath $Source -Force -ErrorAction Stop) {
        if($item.PSIsContainer -and $item.Name -like 'HELP_*' -and $item.Name -ne 'HELP_RU'){continue}
        if(!$item.PSIsContainer -and $item.Name -match '^unins\d+\.(exe|dat|msg)$'){continue}
        Copy-Item -LiteralPath $item.FullName -Destination $Destination -Recurse -Force -ErrorAction Stop
    }
}
function Get-MaintenanceToolFiles([string]$Directory) {
    foreach($item in Get-ChildItem -LiteralPath $Directory -Force -ErrorAction Stop) {
        if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw ('Ссылка в комплекте утилит не поддерживается: '+$item.FullName)}
        if($item.PSIsContainer) {
            if($item.Name -like 'HELP_*' -and $item.Name -ne 'HELP_RU'){continue}
            Get-MaintenanceToolFiles $item.FullName
        } elseif($item.Name -notmatch '^unins\d+\.(exe|dat|msg)$') {$item}
    }
}
function Get-LocalToolCache([string]$SourceTools) {
    $root=[IO.Path]::GetFullPath($SourceTools).TrimEnd('\')+'\'
    $folders=@('CrystalDiskInfo9_6_3_Portable','CrystalDiskMark9','TreeSize Free')
    $inventory=@(foreach($folder in $folders) {
        $source=Join-Path $root $folder
        if(!(Test-Path -LiteralPath $source -PathType Container)){continue}
        foreach($file in Get-MaintenanceToolFiles $source){
            New-Object PSObject -Property @{Relative=$file.FullName.Substring($root.Length);Length=$file.Length;Ticks=$file.LastWriteTimeUtc.Ticks}
        }
    })
    $description=(@($inventory | Sort-Object Relative | ForEach-Object {'{0}|{1}|{2}' -f $_.Relative,$_.Length,$_.Ticks}) -join "`n")
    $sha=[Security.Cryptography.SHA256]::Create()
    try {$key=([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($description)))).Replace('-','')} finally {$sha.Dispose()}
    $base=Join-Path $env:ProgramData 'ServiceMaintenance\ToolCache'
    if(!$script:Admin){$base=Join-Path $env:LOCALAPPDATA 'ServiceMaintenance\ToolCache'}
    foreach($cache in @(Get-ChildItem -LiteralPath $base -ErrorAction SilentlyContinue | Where-Object {$_.PSIsContainer} | Sort-Object Name -Descending)) {
        $marker=Join-Path $cache.FullName 'cache-id.txt'
        if(!(Test-Path -LiteralPath $marker) -or [IO.File]::ReadAllText($marker).Trim() -ne $key){continue}
        $valid=$true
        foreach($file in $inventory) {
            $local=Join-Path $cache.FullName $file.Relative
            if(!(Test-Path -LiteralPath $local -PathType Leaf)){$valid=$false;break}
            if($file.Relative -notmatch '\.(ini|txt)$' -and (Get-Item -LiteralPath $local).Length -ne $file.Length){$valid=$false;break}
        }
        if($valid){Write-Host 'Утилиты уже есть на ПК: используем локальную копию.' -ForegroundColor DarkGray;return $cache.FullName}
    }
    $destination=New-LocalMaintenanceDirectory 'ToolCache'
    Write-Host 'Новый или изменённый комплект: копирование утилит на ПК...' -ForegroundColor DarkGray
    foreach($folder in $folders) {
        $source=Join-Path $root $folder
        if(Test-Path -LiteralPath $source -PathType Container){Copy-MaintenanceTool $source (Join-Path $destination $folder)}
        else {Write-Host ('Утилита отсутствует: '+$source) -ForegroundColor Yellow}
    }
    # Publish only after the entire bundle has been copied successfully.
    $key | Set-Content -LiteralPath (Join-Path $destination 'cache-id.txt') -Encoding ASCII
    return $destination
}
function Initialize-LocalRuntime([string]$SourceRoot,[string]$SourceTools) {
    $copyTimer=[Diagnostics.Stopwatch]::StartNew()
    $sourceDevice=Get-SourceUsbDevice $SourceTools
    $originalSource=$SourceTools
    $packageSource=Join-Path $SourceRoot 'package-source.xml'
    if(Test-Path -LiteralPath $packageSource -PathType Leaf) {
        $source=Import-Clixml -LiteralPath $packageSource
        $originalSource=[string]$source.SourceTools
        $sourceDevice=$source.Device
    }
    $session = New-LocalMaintenanceDirectory 'Sessions'
    $destination = Join-Path $session 'Service'
    New-Item -ItemType Directory -Path $destination | Out-Null
    $files = @('Start.cmd','Start-Win7.cmd','Results.cmd','Results.ps1','Service.ps1','Service-Win7.ps1','Summary.ps1','DiskTools.ps1','Cleanup.ps1','Repair.ps1','Runtime.ps1','RepairWorker.ps1','AdminCredentials.ps1','UserCleanup.ps1','UserCleanupWorker.ps1','NativeDiskMark.cs','NativeUsb.cs','allowed-processes.json','allowed-processes-win7.txt','README.md','Set-WindowsAutomaticUpdates.ps1')
    $files += @('AdminBootstrap.ps1','EventWorker.ps1','Elevation.ps1','allowed-publishers.txt','DiskWorker.ps1','Start-Disks.cmd')
    foreach($file in $files) { Copy-Item -LiteralPath (Join-Path $SourceRoot $file) -Destination $destination -ErrorAction Stop }
    $localTools=Get-LocalToolCache $SourceTools
    Write-Host "Локальная рабочая копия: $session"
    ('CopySeconds={0:N2}' -f $copyTimer.Elapsed.TotalSeconds) | Set-Content -LiteralPath (Join-Path $destination 'timings.txt') -Encoding UTF8
    New-Object PSObject -Property @{ScriptRoot=$destination;ToolsRoot=$localTools;SourceDevice=$sourceDevice;SourceTools=$originalSource}
}
function Get-SourceUsbDevice([string]$Path) {
    try {
        $root=[IO.Path]::GetPathRoot([IO.Path]::GetFullPath($Path))
        if($root -notmatch '^[A-Za-z]:\\$' -or $root.TrimEnd('\') -eq $env:SystemDrive){return $null}
        $volume=Get-WmiObject Win32_LogicalDisk -Filter ("DeviceID='"+$root.TrimEnd('\')+"'") -ErrorAction Stop
        if(!$volume){return $null}
        $disks=@($volume.GetRelated('Win32_DiskPartition') | ForEach-Object {$_.GetRelated('Win32_DiskDrive')} | Sort-Object PNPDeviceID -Unique)
        if($disks.Count -ne 1 -or $disks[0].InterfaceType -ne 'USB' -or !$disks[0].PNPDeviceID){return $null}
        New-Object PSObject -Property @{Root=$root;Id=[string]$disks[0].PNPDeviceID;Model=[string]$disks[0].Model}
    } catch {return $null}
}
function Show-UsbReady([string]$SourceTools,$ExpectedDevice) {
    $driveRoot = [IO.Path]::GetPathRoot([IO.Path]::GetFullPath($SourceTools))
    try {
        if(!$ExpectedDevice){Write-Host 'Исходный USB-накопитель не определён. Автоизвлечение пропущено.' -ForegroundColor Yellow; return}
        $current=Get-SourceUsbDevice $SourceTools
        if(!$current){Write-Host 'Исходный USB-накопитель уже отключён или недоступен.'; return}
        if($current.Id -ne $ExpectedDevice.Id){throw 'Устройство с этой буквой изменилось; извлечение отменено.'}
        $localRoot=[IO.Path]::GetPathRoot([IO.Path]::GetFullPath($ScriptRoot))
        $localDevice=Get-SourceUsbDevice $ScriptRoot
        if($localRoot -eq $driveRoot -or ($localDevice -and $localDevice.Id -eq $ExpectedDevice.Id)){throw 'Рабочая копия находится на том же USB-устройстве.'}
        $running = @(Get-WmiObject Win32_Process -ErrorAction Stop | Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($driveRoot,[StringComparison]::OrdinalIgnoreCase) })
        if($running.Count) {
            Write-Host ('С исходного диска ещё работают: '+(($running | ForEach-Object { $_.Name }) -join ', ')) -ForegroundColor Yellow
            Write-Host 'Закройте эти программы перед извлечением флешки. Этот комплект уже работает с локальной копии.'
            return
        }
        if(!('SafeUsbEject' -as [type])){Add-Type -TypeDefinition ([IO.File]::ReadAllText((Join-Path $ScriptRoot 'NativeUsb.cs'),[Text.Encoding]::UTF8)) -ErrorAction Stop}
        Write-Host "Безопасное извлечение $driveRoot..."
        $result=[SafeUsbEject]::Request($ExpectedDevice.Id)
        if($result.Code -ne 0){throw ('Windows отказала в извлечении (код {0}, причина {1}): {2}' -f $result.Code,$result.Veto,$result.Detail)}
        Write-Host "`n========================================" -ForegroundColor Green
        Write-Host ' ФЛЕШКА ИЗВЛЕЧЕНА - МОЖНО ЗАБИРАТЬ' -ForegroundColor Green
        Write-Host "========================================`n" -ForegroundColor Green
        Write-Host 'Обслуживание продолжается с локального диска.'
    } catch { Write-Host ('Флешка не извлечена: '+$_.Exception.Message) -ForegroundColor Yellow; Write-Host 'Обслуживание продолжится. Закройте файлы на флешке и извлеките её через Windows.' }
}
function Start-RepairBackground([switch]$TestOnly,[switch]$WithCleanup) {
    if(!$script:Admin) { throw 'Для фонового восстановления нужны права администратора.' }
    if(Get-Command Assert-DiskTestIdle -ErrorAction SilentlyContinue) { Assert-DiskTestIdle }
    $folder = New-LocalMaintenanceDirectory 'Jobs'
    foreach($file in @('Repair.ps1','RepairWorker.ps1','Cleanup.ps1')) { Copy-Item -LiteralPath (Join-Path $ScriptRoot $file) -Destination $folder -ErrorAction Stop }
    $root64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($folder))
    $testFlag = ''
    if($TestOnly) { $testFlag=' -TestOnly' }
    if($WithCleanup) { $testFlag += ' -WithCleanup' }
    $user64=[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes([string]$OriginalUserJob))
    $command = "`$u=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('$user64')); `$r=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('$root64')); & ([scriptblock]::Create([IO.File]::ReadAllText((Join-Path `$r 'RepairWorker.ps1'),[Text.Encoding]::UTF8))) -JobRoot `$r -OriginalUserJob `$u$testFlag"
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
    $p = Start-Process powershell.exe -WindowStyle Hidden -WorkingDirectory $folder -ArgumentList "-NoProfile -ExecutionPolicy Bypass -EncodedCommand $encoded" -PassThru -ErrorAction Stop
    $script:LastRepairFolder = $folder
    $folder | Set-Content -LiteralPath (Join-Path $ScriptRoot 'background-job.txt') -Encoding UTF8
    if(!$script:CompactOutput){
        Write-Host "Фоновый процесс запущен (PID $($p.Id)). Статус и журнал: $folder"
        Write-Host 'Консоль можно закрыть. Не выключайте ПК и не выходите из учётной записи до завершения фоновых работ.'
    }
    return $folder
}
function Show-RepairStatus {
    $localStatus=Join-Path $ScriptRoot 'repair-status.txt'
    if(Test-Path -LiteralPath $localStatus) {Write-Host ([IO.File]::ReadAllText($localStatus)); return}
    if(!$script:LastRepairFolder) {
        $base = Join-Path $env:ProgramData 'ServiceMaintenance\Jobs'
        if(Test-Path -LiteralPath $base) { $latest = Get-ChildItem -LiteralPath $base | Where-Object { $_.PSIsContainer } | Sort-Object Name -Descending | Select-Object -First 1; if($latest){$script:LastRepairFolder=$latest.FullName} }
    }
    if(!$script:LastRepairFolder) { Write-Host 'Заданий пока нет.'; return }
    $status = Join-Path $script:LastRepairFolder 'status.txt'
    if(Test-Path -LiteralPath $status) { Get-Content -LiteralPath $status -Encoding UTF8 | Out-Host }
    else { Write-Host 'Процесс ещё запускается. Если статус не появится, проверьте запуск PowerShell.' }
    Write-Host "Каталог задания: $script:LastRepairFolder"
}
function Invoke-AutoMaintenance {
    $log = Join-Path $ScriptRoot 'summary.log'
    Start-Transcript -Path $log -Force | Out-Null
    try {
        $script:CompactOutput=$true
        Write-Host ('Исходный пользователь: '+$OriginalIdentity) -ForegroundColor DarkGray
        Write-Host ('Обслуживание: '+[Security.Principal.WindowsIdentity]::GetCurrent().Name+' | повышенные права: '+$script:Admin) -ForegroundColor Cyan
        $script:DiskResult=$null; $script:DiskFailure=$null
        $script:CleanupResult=$null; $script:CleanupFailure=$null
        $script:LastRepairFolder=$null
        $script:Snapshot=@{Smart=@();Notes=@()}
        $script:PendingDiskTest=$null
        $script:DiskWorker=$null
        $script:PreserveDiskSnapshot=$true
        Get-ServiceSnapshot -Live -StartDiskTest
        if($script:DiskWorker -and !$script:DiskFailure){try {Complete-DiskToolsBackground} catch {$script:DiskFailure=$_.Exception.Message}}
        Start-LowSpaceTreeSize
        Section 'ФОНОВОЕ ОБСЛУЖИВАНИЕ'
        if($script:Admin){try { Start-RepairBackground -WithCleanup | Out-Null } catch { $script:CleanupFailure=('Фоновые работы не запущены: '+$_.Exception.Message) }}
        else {
            try {Start-LimitedCleanup} catch {$script:CleanupFailure=('Пользовательская очистка не запущена: '+$_.Exception.Message)}
            Write-Host 'DISM/SFC и очистка обновлений не запускаются: нет прав администратора.' -ForegroundColor Yellow
        }
        $script:Snapshot | Export-Clixml -Path (Join-Path $ScriptRoot 'details.xml')
        Get-SummaryLines | Set-Content -LiteralPath (Join-Path $ScriptRoot 'brief.txt') -Encoding UTF8
        if($script:CleanupFailure){Write-Host $script:CleanupFailure -ForegroundColor Yellow}
        elseif($script:Admin) {Write-Host 'Очистка и восстановление запущены отдельно; их завершение пока не подтверждено.' -ForegroundColor Cyan}
        else {Write-Host 'Открывается очистка своего профиля. Выберите категории в штатном окне Windows.' -ForegroundColor Cyan}
        Write-Host ('Проверить фоновые работы: '+(Join-Path $ScriptRoot 'Results.cmd')) -ForegroundColor DarkGray
        Write-Host 'Enter: закрыть консоль и завершённые дисковые утилиты. TreeSize и фоновые работы останутся.' -ForegroundColor DarkGray
        Show-InitialSection 2
        Update-MaintenanceHistory
        Show-FinalSummary
    } finally { Stop-Transcript | Out-Null }
}
function Start-LowSpaceTreeSize {
    foreach($volume in @($script:Snapshot.Volumes)) {
        if(!$volume.Size -or (100*(1-$volume.FreeSpace/$volume.Size)) -lt 80){continue}
        try {
            $exe=Find-Tool 'TreeSize Free\TreeSizeFree.exe'
            Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -ArgumentList ($volume.DeviceID+'\') -ErrorAction Stop | Out-Null
            $script:Snapshot.Notes += ('TreeSize: запущен анализ '+$volume.DeviceID+'; окно остаётся открытым.')
        } catch {$script:Snapshot.Notes += ('TreeSize '+$volume.DeviceID+': '+$_.Exception.Message)}
    }
}
function Start-LimitedCleanup {
    if(Get-Command Assert-DiskTestIdle -ErrorAction SilentlyContinue){Assert-DiskTestIdle}
    if(Get-Process cleanmgr,dism,sfc -ErrorAction SilentlyContinue){throw 'Другая очистка или восстановление уже выполняется.'}
    if(!$OriginalUserJob) {
        . ([scriptblock]::Create([IO.File]::ReadAllText((Join-Path $ScriptRoot 'UserCleanup.ps1'),[Text.Encoding]::UTF8)))
        $OriginalUserJob=Start-UserCleanupWaiting
    }
    $signal=Join-Path $OriginalUserJob 'go.txt'
    if(Test-Path -LiteralPath $signal){throw 'Это пользовательское задание уже получило команду.'}
    'interactive' | Set-Content -LiteralPath $signal -Encoding ASCII
    $OriginalUserJob | Set-Content -LiteralPath (Join-Path $ScriptRoot 'user-cleanup-job.txt') -Encoding UTF8
}
function Invoke-RepairInConsole {
    $statusPath=Join-Path $ScriptRoot 'repair-status.txt'
    $script:RepairFailed=$false; $script:RepairRestartRequired=$false
    function Section([string]$Text) {
        Write-Host "`n=== $Text ===" -ForegroundColor Cyan
        $Text | Set-Content -LiteralPath $statusPath -Encoding UTF8
    }
    try {
        Invoke-SystemRepair
        $message='Команды завершились. Итог проверки смотрите в сообщении SFC выше.'
        if($script:RepairFailed){$message='Завершено с ошибками. Смотрите вывод DISM/SFC.'}
        if($script:RepairRestartRequired){$message='Требуется перезагрузка; автоматически не выполнялась.'}
        $message | Set-Content -LiteralPath $statusPath -Encoding UTF8
    } catch {
        ('Не выполнено: '+$_.Exception.Message) | Set-Content -LiteralPath $statusPath -Encoding UTF8
        throw
    }
}
