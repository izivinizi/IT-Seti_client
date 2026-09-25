function Test-AllowedPublisher([string]$Publisher) {
    if(!$Publisher){return $false}
    $value=($Publisher -replace '[^\p{L}\p{Nd}]','').ToUpperInvariant()
    foreach($entry in $script:AllowedPublishers) {
        $prefix=$entry.EndsWith('*')
        $normalized=($entry -replace '[^\p{L}\p{Nd}]','').ToUpperInvariant()
        if($normalized -and (($prefix -and $value.StartsWith($normalized)) -or (!$prefix -and $value -eq $normalized))){return $true}
    }
    return $false
}
function Get-MaintenanceComparison($Previous,$Current) {
    if(!$Previous){'Первая проверка с историей: сохранена исходная точка.';return}
    'Предыдущая проверка: {0:dd.MM.yyyy HH:mm}' -f $Previous.CapturedAt
    $old=$Previous.Snapshot; $new=$Current.Snapshot
    if($null -ne $old.Load -and $null -ne $new.Load){'CPU: {0:N0}% -> {1:N0}% (разовые замеры нагрузки).' -f $old.Load,$new.Load}
    if($old.TotalRAM -gt 0 -and $new.TotalRAM -gt 0 -and $null -ne $old.FreeRAM -and $null -ne $new.FreeRAM){'ОЗУ: {0:N1} -> {1:N1} ГБ; занято {2:N0}% -> {3:N0}%.' -f $old.TotalRAM,$new.TotalRAM,(100*(1-$old.FreeRAM/$old.TotalRAM)),(100*(1-$new.FreeRAM/$new.TotalRAM))}
    foreach($v in $new.Volumes) {
        if(!$v.VolumeSerialNumber){continue}
        $match=@($old.Volumes | Where-Object {$_.VolumeSerialNumber -eq $v.VolumeSerialNumber -and $_.Size -eq $v.Size})
        if($match.Count -eq 1){'Раздел {0}: свободно {1:N1} -> {2:N1} ГБ; изменение {3:+0.0;-0.0;0.0} ГБ между проверками, не результат очистки.' -f $v.DeviceID,($match[0].FreeSpace/1GB),($v.FreeSpace/1GB),(($v.FreeSpace-$match[0].FreeSpace)/1GB)}
    }
    foreach($disk in $new.Smart) {
        $matches=@($old.Smart | Where-Object {$_.Model -eq $disk.Model})
        if($matches.Count -eq 1 -and @($new.Smart | Where-Object {$_.Model -eq $disk.Model}).Count -eq 1 -and $matches[0].Status -ne $disk.Status){'SMART {0}: {1} -> {2}.' -f $disk.Model,$matches[0].Status,$disk.Status}
    }
    foreach($disk in $new.Disks) {
        $matches=@($old.Disks | Where-Object {$_.FriendlyName -eq $disk.FriendlyName})
        if($matches.Count -eq 1 -and $matches[0].HealthStatus -ne $disk.HealthStatus){'Состояние Windows {0}: {1} -> {2}.' -f $disk.FriendlyName,$matches[0].HealthStatus,$disk.HealthStatus}
    }
    if($Previous.Days -eq $Current.Days) {
        $oldKeys=@($old.Events | ForEach-Object {'{0}|{1}|{2}' -f $_.ProviderName,$_.Id,$_.Level})
        $newErrors=@($new.Events | Where-Object {$_.Level -le 2 -and $oldKeys -notcontains ('{0}|{1}|{2}' -f $_.ProviderName,$_.Id,$_.Level)} | Group-Object ProviderName,Id,Level)
        if($newErrors.Count){
            'Типы ошибок, которых не было в прошлой выборке: '+$newErrors.Count
            foreach($g in ($newErrors | Select-Object -First 5)){'  {0}, ID {1}, уровень {2}' -f $g.Group[0].ProviderName,$g.Group[0].Id,$g.Group[0].Level}
        } else {'Новых типов ошибок в доступных выборках не найдено.'}
        if($old.EventLimited -or $new.EventLimited -or $old.EventUnavailable -or $new.EventUnavailable){'Сравнение событий ограничено доступными выборками, не всей историей.'}
    } else {'События не сравнивались: изменён период проверки.'}
    if($Previous.DiskResult -and $Current.DiskResult -and $Previous.SystemVolumeSerial -and $Previous.SystemVolumeSerial -eq $Current.SystemVolumeSerial -and $Previous.DiskResult.MediaType -eq $Current.DiskResult.MediaType){'SEQ чтение: {0} -> {1}; запись: {2} -> {3} МБ/с. Скорость зависит от текущей нагрузки.' -f $Previous.DiskResult.Read,$Current.DiskResult.Read,$Previous.DiskResult.Write,$Current.DiskResult.Write}
}
function Update-MaintenanceHistory {
    Section 'СРАВНЕНИЕ С ПРОШЛОЙ ПРОВЕРКОЙ'
    try {
        $current=@{Schema=1;Computer=$env:COMPUTERNAME;CapturedAt=(Get-Date);Days=$Days;Snapshot=$script:Snapshot;DiskResult=$script:DiskResult;SystemVolumeSerial=(@($script:Snapshot.Volumes | Where-Object {$_.DeviceID -eq $env:SystemDrive} | Select-Object -First 1).VolumeSerialNumber)}
        $previous=$null
        $roots=@((Join-Path $env:ProgramData 'ServiceMaintenance\Sessions'),(Join-Path $env:LOCALAPPDATA 'ServiceMaintenance\Sessions'))
        foreach($root in $roots) {
            foreach($session in @(Get-ChildItem -LiteralPath $root -ErrorAction SilentlyContinue | Where-Object {$_.PSIsContainer} | Sort-Object Name -Descending)) {
                $file=Join-Path $session.FullName 'Service\history.xml'
                if($file -eq (Join-Path $ScriptRoot 'history.xml') -or !(Test-Path -LiteralPath $file)){continue}
                try {
                    if((Get-Item -LiteralPath $file).Length -gt 10MB){continue}
                    $candidate=Import-Clixml -LiteralPath $file -ErrorAction Stop
                    if($candidate.Schema -eq 1 -and $candidate.Computer -eq $env:COMPUTERNAME -and $candidate.CapturedAt -lt $current.CapturedAt){
                        if(!$previous -or $candidate.CapturedAt -gt $previous.CapturedAt){$previous=$candidate}
                        break
                    }
                } catch {}
            }
        }
        $lines=@(Get-MaintenanceComparison $previous $current)
        $lines | ForEach-Object {Write-Host $_}
        $lines | Set-Content -LiteralPath (Join-Path $ScriptRoot 'comparison.txt') -Encoding UTF8
        $temporary=Join-Path $ScriptRoot 'history.pending.xml'
        $current | Export-Clixml -LiteralPath $temporary -ErrorAction Stop
        Move-Item -LiteralPath $temporary -Destination (Join-Path $ScriptRoot 'history.xml') -Force -ErrorAction Stop
    } catch {Write-Host ('История не сохранена: '+$_.Exception.Message) -ForegroundColor Yellow}
}
function Get-ServiceSnapshot([switch]$Live,[switch]$StartDiskTest) {
    $scanTimer=[Diagnostics.Stopwatch]::StartNew()
    Write-Host 'Проверка: оборудование, нагрузка, процессы и журналы...' -ForegroundColor Cyan
    $previousSmart=@(); $previousNotes=@()
    if($script:PreserveDiskSnapshot){$previousSmart=@($script:Snapshot.Smart | Where-Object {$_});$previousNotes=@($script:Snapshot.Notes | Where-Object {$_})}
    $script:PreserveDiskSnapshot=$false
    $script:Snapshot = @{Notes=$previousNotes; Processes=@(); Events=@(); Volumes=@(); Disks=@(); Smart=$previousSmart; CPU='нет данных'; GPU='нет данных'; CpuTemperatureC=$null; CpuTemperatureStatus=$null; MemoryType='нет данных'; TotalRAM=$null; FreeRAM=$null; Load=$null; LastBootAt=$null; EventLimited=$false; EventUnavailable=0; ProcessUnavailable=0}
    $s=$script:Snapshot
    $script:WindowsUpdateStatus=$null
    if($script:PendingDiskTest){$s.Notes += 'Нагрузка CPU/ОЗУ измерена во время дискового теста, не в простое.'}
    $s.PublisherSkipped=0; $s.PublisherMetadataSkipped=0
    try {
        $s.CPU=((Get-WmiObject Win32_Processor -ErrorAction Stop | ForEach-Object {$_.Name.Trim()}) -join ', ')
        $s.GPU=((Get-WmiObject Win32_VideoController -ErrorAction Stop | Where-Object {$_.Name -notmatch 'Virtual|Parsec|USB Mobile'} | ForEach-Object {$_.Name}) -join ', ')
        $s.CpuTemperatureC=Get-CpuTemperatureC
        if(!$s.GPU) {$s.GPU='только виртуальные адаптеры / нет данных'}
        try {
            $types=@(Get-WmiObject Win32_PhysicalMemory -ErrorAction Stop | ForEach-Object {
                switch ([int]$_.SMBIOSMemoryType) { 26 {'DDR4'} 34 {'DDR5'} 24 {'DDR3'} 21 {'DDR2'} default {switch ([int]$_.MemoryType) { 26 {'DDR4'} 24 {'DDR3'} 21 {'DDR2'} default {$null} }} }
            } | Where-Object {$_} | Select-Object -Unique)
            if($types.Count){$s.MemoryType=$types -join ', '}
        } catch {}
        $os=Get-WmiObject Win32_OperatingSystem -ErrorAction Stop
        $windowsKey=Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' -ErrorAction SilentlyContinue
        $s.WindowsEdition=[string]$windowsKey.ProductName
        $s.WindowsRelease=[string]$(if($windowsKey.DisplayVersion){$windowsKey.DisplayVersion}else{$windowsKey.ReleaseId})
        $buildText=[string]$(if($windowsKey.CurrentBuildNumber){$windowsKey.CurrentBuildNumber}else{$windowsKey.CurrentBuild})
        $buildNumber=0
        if([int]::TryParse($buildText,[ref]$buildNumber)){$s.WindowsBuild=$buildNumber}
        if($script:Admin -and !$script:SkipWindowsUpdateChange -and (Test-Path -LiteralPath (Join-Path $ScriptRoot 'Set-WindowsAutomaticUpdates.ps1'))) {
            try {$script:WindowsUpdateStatus=& (Join-Path $ScriptRoot 'Set-WindowsAutomaticUpdates.ps1') -Action Disable | Select-Object -Last 1}
            catch {$script:WindowsUpdateStatus='не удалось отключить автообновления: '+$_.Exception.Message}
        }
        try {$s.LastBootAt=([DateTimeOffset]([Management.ManagementDateTimeConverter]::ToDateTime($os.LastBootUpTime))).ToString('o')} catch {$s.Notes+=('Время последней загрузки: '+$_.Exception.Message)}
        $s.TotalRAM=[double]$os.TotalVisibleMemorySize/1MB
        $loads=@(); $free=@()
        for($i=0;$i -lt $Samples;$i++) {
            $counter=Get-WmiObject Win32_PerfFormattedData_PerfOS_Processor -Filter "Name='_Total'" -ErrorAction Stop
            if(!$counter) {throw 'Счётчик CPU недоступен'}
            $loads += [double]$counter.PercentProcessorTime
            $free += [double](Get-WmiObject Win32_OperatingSystem -ErrorAction Stop).FreePhysicalMemory/1MB
            if($i -lt $Samples-1) {Start-Sleep -Seconds 1}
        }
        $s.Load=($loads | Measure-Object -Average).Average
        $s.FreeRAM=($free | Measure-Object -Average).Average
        $measuredTemperature=Get-CpuTemperatureC
        if($null -ne $measuredTemperature -and ($null -eq $s.CpuTemperatureC -or $measuredTemperature -gt $s.CpuTemperatureC)){$s.CpuTemperatureC=$measuredTemperature}
        if($s.Load -ge 85) {$s.Notes += 'Высокая загрузка CPU: проверьте процессы на обычной рабочей нагрузке.'}
        if(Get-Process dism,sfc -ErrorAction SilentlyContinue) {$s.Notes += 'Уже идёт восстановление Windows; оно влияет на текущую нагрузку.'}
    } catch {$s.Notes += ('Оборудование/нагрузка: '+$_.Exception.Message)}
    if($Live){Write-Host ("ПК: $env:COMPUTERNAME"); Show-InitialSection 1}
    if($StartDiskTest) {
        if($script:Admin){try {Start-DiskToolsBackground} catch {$script:DiskFailure=$_.Exception.Message}}
        else {$script:DiskFailure='Пропущен: для автоматического теста диска нужны права администратора.'}
    }
    try {
        $s.Volumes=@(Get-WmiObject Win32_LogicalDisk -Filter 'DriveType=3' -ErrorAction Stop | Select-Object DeviceID,Size,FreeSpace,VolumeSerialNumber)
        if(Get-Command Get-PhysicalDisk -ErrorAction SilentlyContinue) {
            $s.Disks=@(Get-PhysicalDisk -ErrorAction Stop | Where-Object {$_.BusType -ne 'USB'} | Select-Object FriendlyName,MediaType,HealthStatus)
        } else {
            $s.Disks=@(Get-WmiObject Win32_DiskDrive -ErrorAction Stop | Select-Object @{n='FriendlyName';e={$_.Model}},@{n='MediaType';e={'тип см. CDI'}},@{n='HealthStatus';e={$_.Status}})
        }
    } catch {$s.Notes += ('Диски: '+$_.Exception.Message)}
    if($Live -and (Get-Command Save-Result -ErrorAction SilentlyContinue)){Save-Result 'partial.json' -Pending}
    $eventProcess=$null
    try {
        $folder=Join-Path (Join-Path $env:LOCALAPPDATA 'ServiceMaintenance\EventChecks') ([guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $folder -Force | Out-Null
        $worker=Join-Path $folder 'EventWorker.ps1'
        Copy-Item -LiteralPath (Join-Path $ScriptRoot 'EventWorker.ps1') -Destination $worker
        $arguments='-NoProfile -ExecutionPolicy Bypass -File "'+$worker+'" -Folder "'+$folder+'" -Days '+$Days
        $eventProcess=Start-Process powershell.exe -WindowStyle Hidden -WorkingDirectory $folder -ArgumentList $arguments -PassThru
    } catch {$s.Notes += ('Запуск проверки журналов: '+$_.Exception.Message)}
    $processTimer=[Diagnostics.Stopwatch]::StartNew()
    try {
        $script:AllowedPublishers=@()
        $publisherFile=Join-Path $ScriptRoot 'allowed-publishers.txt'
        if(Test-Path -LiteralPath $publisherFile){$script:AllowedPublishers=@([IO.File]::ReadAllLines($publisherFile,[Text.Encoding]::UTF8) | ForEach-Object {$_.Trim()} | Where-Object {$_ -and !$_.StartsWith('#')})}
        if($PSVersionTable.PSVersion.Major -ge 3) {$allowed=([IO.File]::ReadAllText((Join-Path $ScriptRoot 'allowed-processes.json')) | ConvertFrom-Json).allowedNames}
        else {$allowed=@(Get-Content -LiteralPath (Join-Path $ScriptRoot 'allowed-processes-win7.txt'))}
        $candidates=@(foreach($p in (Get-Process | Sort-Object ProcessName -Unique)) {
            if($allowed -contains $p.ProcessName) {continue}
            $path=$null
            try {$path=$p.Path} catch {}
            if(!$path) {$s.ProcessUnavailable++; continue}
            New-Object PSObject -Property @{Process=$p;Path=$path}
        })
        $signatures=Get-ProcessSignatures @($candidates | ForEach-Object {$_.Path})
        foreach($candidate in $candidates) {
            $p=$candidate.Process;$path=$candidate.Path
            $sig=$signatures[$path]
            $signer=''
            if($sig.SignerCertificate) {$signer=$sig.SignerCertificate.GetNameInfo('SimpleName',$false)}
            if($path.StartsWith($env:windir+'\',[StringComparison]::OrdinalIgnoreCase) -and $sig.Status -eq 'Valid' -and $signer -match 'Microsoft') {continue}
            $description=$p.ProcessName; $company=''
            try {
                $version=[Diagnostics.FileVersionInfo]::GetVersionInfo($path)
                if($version.FileDescription){$description=$version.FileDescription}
                elseif($version.ProductName){$description=$version.ProductName}
                $company=$version.CompanyName
            } catch {}
            if($sig.Status -eq 'Valid' -and (Test-AllowedPublisher $signer)){$s.PublisherSkipped++; continue}
            if($sig.Status -eq 'NotSigned' -and (Test-AllowedPublisher $company)){$s.PublisherMetadataSkipped++; continue}
            $s.Processes += New-Object PSObject -Property @{Name=$p.ProcessName;Description=$description;Company=$company;Path=$path;Signature=[string]$sig.Status;Signer=$signer}
        }
    } catch {$s.Notes += ('Процессы: '+$_.Exception.Message)}
    if($Live -and (Get-Command Save-Result -ErrorAction SilentlyContinue)){Save-Result 'partial.json' -Pending}
    try {('ProcessSeconds={0:N2}' -f $processTimer.Elapsed.TotalSeconds) | Add-Content -LiteralPath (Join-Path $ScriptRoot 'timings.txt') -Encoding UTF8} catch {}
    if($Live){Show-InitialSection 3; Write-Host 'Процессы вне списка не обязательно вредоносны. Проверяются журналы...' -ForegroundColor DarkGray}
    try {
        if(!$eventProcess){throw 'Работник журналов не запущен.'}
        if(!$eventProcess.WaitForExit(30000)) {
            $eventProcess.Kill()
            $eventProcess.WaitForExit()
            $s.Notes += 'Поиск событий ограничен 30 секундами; часть журналов не проверена.'
        }
        foreach($index in 0..3) {
            $path=Join-Path $folder ([string]$index+'.xml')
            if(!(Test-Path -LiteralPath $path)){$s.EventUnavailable++; continue}
            $result=Import-Clixml -LiteralPath $path
            $s.Events += $result.Events
            if($result.Limited){$s.EventLimited=$true}
            if($result.Error){$s.EventUnavailable++; $s.Notes += $result.Error}
        }
    } catch {$s.EventUnavailable++; $s.Notes += ('Журналы: '+$_.Exception.Message)}
    if($Live){Show-InitialSection 4}
    try {('SnapshotSeconds={0:N2}' -f $scanTimer.Elapsed.TotalSeconds) | Add-Content -LiteralPath (Join-Path $ScriptRoot 'timings.txt') -Encoding UTF8} catch {}
}
function Get-ProcessSignatures([string[]]$Paths) {
    $results=@{}
    if(!$Paths.Count){return $results}
    $pool=[RunspaceFactory]::CreateRunspacePool(1,2)
    $tasks=@()
    try {
        $pool.Open()
        foreach($path in @($Paths | Sort-Object -Unique)) {
            $shell=[PowerShell]::Create();$shell.RunspacePool=$pool
            $null=$shell.AddScript('param($path) Get-AuthenticodeSignature -FilePath $path -ErrorAction Stop').AddArgument($path)
            try {$tasks+=@(@{Shell=$shell;Handle=$shell.BeginInvoke();Path=$path})} catch {$shell.Dispose();throw}
        }
        foreach($task in $tasks) {
            $signature=$null
            try {$values=$task.Shell.EndInvoke($task.Handle);if($values.Count){$signature=$values[0]}} catch {}
            if(!$signature){$signature=New-Object PSObject -Property @{Status='UnknownError';SignerCertificate=$null}}
            $results[$task.Path]=$signature
        }
    } finally {foreach($task in $tasks){$task.Shell.Dispose()};$pool.Dispose()}
    return $results
}
function Get-SummaryLines {
    $s=$script:Snapshot
    if(!$s) {return 'Сводка ещё не собрана.'}
    "ПК: $env:COMPUTERNAME | $(Get-Date -Format 'dd.MM.yyyy HH:mm')"
    '01 | РЕСУРСЫ СИСТЕМЫ'
    if($s.WindowsEdition){'Windows: {0}, версия {1}, сборка {2}' -f $s.WindowsEdition,$s.WindowsRelease,$s.WindowsBuild}
    if($script:WindowsUpdateStatus){'Автообновления: '+$script:WindowsUpdateStatus}
    'CPU: '+$s.CPU+$(if($null -ne $s.Load){' | {0:N0}%' -f $s.Load})+$(if($null -ne $s.CpuTemperatureC){' | {0:N0} °C' -f $s.CpuTemperatureC}else{' | температура недоступна'})
    if($null -ne $s.FreeRAM -and $s.TotalRAM -gt 0) {'ОЗУ: {0:N1} ГБ | свободно {1:N1} ГБ | занято {2:N0}%' -f $s.TotalRAM,$s.FreeRAM,(100*(1-$s.FreeRAM/$s.TotalRAM))}
    else {'ОЗУ: замер недоступен'}
    'GPU: '+$s.GPU
    '02 | ДИСКИ И МЕСТО'
    $systemDrive=$env:SystemDrive
    $systemSmart=@($s.Smart | Where-Object {[regex]::IsMatch([string]$_.Letters,('(?i)(?<![A-Z])'+[regex]::Escape($systemDrive)))} | Select-Object -First 1)
    $systemVolume=$s.Volumes | Where-Object {$_.DeviceID -eq $systemDrive} | Select-Object -First 1
    $systemPhysical=$null
    if($systemSmart.Count){$systemPhysical=$s.Disks | Where-Object {$_.FriendlyName -eq $systemSmart[0].Model} | Select-Object -First 1}
    elseif(@($s.Disks).Count -eq 1){$systemPhysical=$s.Disks[0]}
    $diskParts=@($systemDrive)
    if($systemSmart.Count){$diskParts+=([string]$systemSmart[0].Model);if($systemSmart[0].MediaType -and $systemSmart[0].MediaType -ne 'Unknown'){$diskParts+=([string]$systemSmart[0].MediaType)};$diskParts+=('SMART '+[string]$systemSmart[0].Status);if($systemSmart[0].TransferMode){$diskParts+=([string]$systemSmart[0].TransferMode -replace '\s*\|\s*','/')}}
    elseif($systemPhysical){$diskParts+=([string]$systemPhysical.FriendlyName);if($systemPhysical.MediaType){$diskParts+=([string]$systemPhysical.MediaType)};$diskParts+=('Windows '+[string]$systemPhysical.HealthStatus)}
    if($systemSmart.Count -and $null -ne $systemSmart[0].PowerOnHours){$hours=[long]$systemSmart[0].PowerOnHours;$days=[math]::Floor($hours/24);$diskParts+=('{0:N0} ч (~{1} г. {2} дн.)' -f $hours,[math]::Floor($days/365),($days%365))}
    if($systemVolume -and $systemVolume.Size){$used=100*(1-$systemVolume.FreeSpace/$systemVolume.Size);$diskParts+=('{0:N1}/{1:N1} ГБ свободно, занято {2:N0}%' -f ($systemVolume.FreeSpace/1GB),($systemVolume.Size/1GB),$used)}
    if($script:DiskResult){$diskParts+=('SEQ чтение {0} МБ/с' -f $script:DiskResult.Read)}
    elseif($script:DiskFailure){$diskParts+=('тест: '+$script:DiskFailure)}
    'Системный диск: '+($diskParts -join ' | ')
    '03 | НЕСТАНДАРТНЫЕ ПРОЦЕССЫ'
    'Процессы вне списка: {0}; без доступа к файлу: {1}' -f @($s.Processes).Count,$s.ProcessUnavailable
    if($s.PublisherSkipped -or $s.PublisherMetadataSkipped){'Исключено по издателю: {0}; по полю файла без подписи: {1} (не проверка подлинности).' -f $s.PublisherSkipped,$s.PublisherMetadataSkipped}
    if(@($s.Processes).Count){Get-ProcessTableLines $s.Processes}
    '04 | ВАЖНЫЕ СОБЫТИЯ'
    'Журналы за {0} дн.: критических {1}, ошибок {2}, предупреждений {3}{4}' -f $Days,@($s.Events | Where-Object {$_.Level -eq 1}).Count,@($s.Events | Where-Object {$_.Level -eq 2}).Count,@($s.Events | Where-Object {$_.Level -eq 3}).Count,$(if($s.EventLimited -or $s.EventUnavailable){' (неполные данные)'})
    $groups=$s.Events | Group-Object ProviderName,Id,Level | Sort-Object @{e={($_.Group | Measure-Object Level -Minimum).Minimum};Ascending=$true},@{e='Count';Descending=$true}
    foreach($g in ($groups | Select-Object -First 3)) {
        $e=$g.Group[0]
        '  {0}, ID {1}: {2} раз' -f $e.ProviderName,$e.Id,$g.Count
        $example=$g.Group | Where-Object {$_.Message} | Sort-Object TimeCreated -Descending | Select-Object -First 1
        if($example) {
            '  Пример события: {0:dd.MM.yyyy HH:mm:ss}' -f $example.TimeCreated
            foreach($line in ([string]$example.Message -split '\r?\n')) {
                $clean=($line -replace '[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]',' ').Trim()
                if($clean){'    '+$clean}
            }
        } else {'    Текст сообщения не получен: описание не было прочитано до завершения проверки журналов.'}
        ''
    }
    '05 | ИТОГ ДИАГНОСТИКИ'
    Get-FinalSummaryLines
}
function Get-FinalSummaryLines {
    $s=$script:Snapshot
    if($s.WindowsBuild -and $s.WindowsBuild -lt 17763){'Внимание: Windows ниже 1809: {0}, версия {1}, сборка {2}.' -f $s.WindowsEdition,$s.WindowsRelease,$s.WindowsBuild}
    $alerts=@(Get-CriticalFindings)
    if($alerts.Count){$alerts | ForEach-Object {'! '+$_}}
    else {'По проверенным показателям критических признаков не найдено.'}
    if($s.EventUnavailable){'Журналы: не получено выборок: '+$s.EventUnavailable+'.'}
    if($null -eq $s.FreeRAM){'ОЗУ: замер доступной памяти не получен.'}
    if(!@($s.Disks).Count){'Накопители: сведения о состоянии не получены.'}
    if(!@($s.Volumes).Count){'Разделы: сведения о свободном месте не получены.'}
    if(!$script:DiskResult -and !$script:DiskFailure){'Скорость диска: тест не выполнялся или результат не получен; пороги не оценены.'}
    elseif($script:DiskResult -and @('SSD','HDD') -notcontains $script:DiskResult.MediaType){'Порог скорости не проверен: тип системного диска неизвестен.'}
    if($null -eq $s.Smart -or !@($s.Smart).Count){'SMART не получен автоматически: проверьте CrystalDiskInfo.'}
    foreach($note in $s.Notes) {'Внимание: '+$note}

    '06 | ФОНОВОЕ ОБСЛУЖИВАНИЕ'
    if(!$script:Admin){'Без администратора: только пользовательская очистка с выбором категорий; DISM/SFC и очистка обновлений пропущены.'}
    if($script:LastRepairFolder){'Очистка пользователя, системная очистка и DISM/SFC: очередь в фоне.'; 'Статус: '+(Join-Path $script:LastRepairFolder 'status.txt')}
    if($script:CleanupResult){'Очистка: код {0}; изменение свободного места {1:N2} ГБ' -f $script:CleanupResult.ExitCode,$script:CleanupResult.ChangeGB}
    elseif($script:CleanupFailure){'Очистка: '+$script:CleanupFailure}

}

function Get-ProcessTableLines($Processes,[int]$Width=0) {
    if(!$Width){try {$Width=$Host.UI.RawUI.WindowSize.Width} catch {$Width=100}}
    $Width=[math]::Max(60,[math]::Min(160,$Width-1))
    $processWidth=[int][math]::Floor(($Width-4)*0.30)
    $titleWidth=[int][math]::Floor(($Width-4)*0.37)
    $publisherWidth=$Width-4-$processWidth-$titleWidth
    $rows=@($Processes | Sort-Object Name | ForEach-Object {
        $title=$_.Description; if(!$title){$title=$_.Name}
        $publisher='нет данных'
        if($_.Signer -and $_.Signature -eq 'Valid'){$publisher=$_.Signer}
        elseif($_.Company){$publisher=$_.Company+' (из файла)'}
        elseif($_.Signer){$publisher=$_.Signer+' (подпись не подтверждена)'}
        New-Object PSObject -Property @{Title=$title;Process=$_.Name;Publisher=$publisher}
    })
    $columns=@(@{Label='Название';Expression={$_.Title};Width=$titleWidth},@{Label='Процесс';Expression={$_.Process};Width=$processWidth},@{Label='Издатель';Expression={$_.Publisher};Width=$publisherWidth})
    ($rows | Format-Table -Property $columns -Wrap | Out-String -Width $Width).TrimEnd() -split '\r?\n'
}
function Get-CriticalFindings {
    $s=$script:Snapshot
    if(!$s){return}
    if($null -ne $s.FreeRAM -and $s.TotalRAM -gt 0) {
        $used=100*(1-$s.FreeRAM/$s.TotalRAM)
        if($used -ge 80){'ОЗУ занята на {0:N1}% (порог 80%).' -f $used}
    }
    foreach($v in $s.Volumes) {
        if($v.Size -gt 0) {
            $used=100*(1-$v.FreeSpace/$v.Size)
            if($used -ge 80){'Диск {0} занят на {1:N1}% (порог 80%).' -f $v.DeviceID,$used}
        }
    }
    $critical=@($s.Events | Where-Object {$_.Level -eq 1})
    if($critical.Count){'В журналах {0} критических событий за {1} дн.' -f $critical.Count,$Days}
    foreach($d in $s.Disks) {
        if(@('Warning','Unhealthy','Degraded','Pred Fail','Error') -contains [string]$d.HealthStatus){'Тревожное состояние {0}: {1}.' -f $d.FriendlyName,$d.HealthStatus}
    }
    foreach($d in $s.Smart) {
        if($d.Status -match 'Caution|Bad|Тревог|Плох'){'CrystalDiskInfo: {0} - {1}.' -f $d.Model,$d.Status}
        if($null -ne $d.PowerOnHours -and [long]$d.PowerOnHours -gt 60000){'Наработка {0}: {1:N0} ч (более 60 000 ч). Проверьте SMART и резервные копии.' -f $d.Model,$d.PowerOnHours}
        Get-DiskLinkWarning $d
    }
    if($null -ne $s.CpuTemperatureC) {
        if($s.CpuTemperatureC -ge 90){'CPU: критически высокая температура {0:N0} °C (порог 90 °C).' -f $s.CpuTemperatureC}
        elseif($s.CpuTemperatureC -gt 80){'CPU: температура {0:N0} °C выше 80 °C.' -f $s.CpuTemperatureC}
    }
    if($script:DiskResult -and @('SSD','HDD') -contains $script:DiskResult.MediaType) {
        $warningLimit=if($script:DiskResult.MediaType -eq 'HDD'){100}elseif($script:DiskResult.IsNvme){900}else{210}
        $criticalLimit=if($script:DiskResult.MediaType -eq 'HDD'){80}else{180}
        $speed=0.0
        $raw=([string]$script:DiskResult.Read).Replace(',','.')
        if([double]::TryParse($raw,[Globalization.NumberStyles]::Float,[Globalization.CultureInfo]::InvariantCulture,[ref]$speed) -and $speed -lt $warningLimit) {
            $message='{0} {1}: чтение {2:N1} МБ/с, ориентир {3} МБ/с.' -f $script:DiskResult.MediaType,$env:SystemDrive,$speed,$warningLimit
            if($speed -lt $criticalLimit){$message='Критически низкая скорость '+$message}
            else {$message='Внимание: скорость ниже ориентира. '+$message}
            $message
        }
    }
}
function Get-CpuTemperatureC {
    $values=@()
    foreach($namespace in @('root\LibreHardwareMonitor','root\OpenHardwareMonitor')) {
        try {
            foreach($sensor in @(Get-WmiObject -Namespace $namespace -Class Sensor -ErrorAction Stop)) {
                if([string]$sensor.SensorType -ne 'Temperature'){continue}
                $identity=([string]$sensor.Identifier+' '+[string]$sensor.Parent+' '+[string]$sensor.Name)
                if($identity -notmatch '(?i)(/(intelcpu|amdcpu)/\d+/|CPU\s*(Package|Core)|Tctl|Tdie|Package\s*id)'){continue}
                $value=0.0
            if([double]::TryParse([string]$sensor.Value,[Globalization.NumberStyles]::Float,[Globalization.CultureInfo]::InvariantCulture,[ref]$value) -and $value -gt 0 -and $value -le 120){$values+= $value}
            }
        } catch {}
    }
    try {
        foreach($zone in @(Get-WmiObject -Namespace 'root\wmi' -Class MSAcpi_ThermalZoneTemperature -ErrorAction Stop | Where-Object {$_.InstanceName -match '(?i)CPU|PROCESSOR|PROCHOT|TCTL|TDIE'})) {
            $value=([double]$zone.CurrentTemperature/10)-273.15
            if($value -gt 0 -and $value -le 120){$values+=$value}
        }
    } catch {}
    if($values.Count){return [double](($values | Measure-Object -Maximum).Maximum)}
    return $null
}
function Get-DiskLinkWarning($Disk) {
    $parts=@(([string]$Disk.TransferMode) -split '\|')
    if($parts.Count -ne 2){return}
    $current=$parts[0].Trim();$supported=$parts[1].Trim()
    $low=$false
    $a=[regex]::Match($current,'^SATA/(150|300|600)$')
    $b=[regex]::Match($supported,'^SATA/(150|300|600)$')
    if($a.Success -and $b.Success){$low=[int]$a.Groups[1].Value -lt [int]$b.Groups[1].Value}
    else {
        $a=[regex]::Match($current,'^PCIe ([1-6])\.0 x(1|2|4|8|16)$')
        $b=[regex]::Match($supported,'^PCIe ([1-6])\.0 x(1|2|4|8|16)$')
        if($a.Success -and $b.Success){$low=([int]$a.Groups[1].Value -lt [int]$b.Groups[1].Value) -or ([int]$a.Groups[2].Value -lt [int]$b.Groups[2].Value)}
    }
    if($low){'Ограничение интерфейса {0}: работает {1}, диск поддерживает {2}. Проверьте порт/слот, контроллер и подключение; возможности ПК не подтверждены.' -f $Disk.Model,$current,$supported}
}
function ConvertFrom-CdiReport([string]$Text) {
    foreach($block in ([regex]::Split($Text,'(?m)(?=^\s*Model\s*:)'))) {
        $model=[regex]::Match($block,'(?m)^\s*Model\s*:\s*(.+)$')
        $health=[regex]::Match($block,'(?m)^\s*Health Status\s*:\s*(.+)$')
        if(!$model.Success -or !$health.Success){continue}
        $letters=[regex]::Match($block,'(?m)^\s*Drive Letter\s*:\s*(.*)$').Groups[1].Value.Trim()
        $rotation=[regex]::Match($block,'(?m)^\s*Rotation Rate\s*:\s*(.*)$').Groups[1].Value
        $interface=[regex]::Match($block,'(?m)^\s*Interface\s*:\s*(.*)$').Groups[1].Value
        $transfer=[regex]::Match($block,'(?m)^[ \t]*Transfer Mode[ \t]*:[ \t]*([^\r\n]*)').Groups[1].Value.Trim()
        $powerOn=[regex]::Match($block,'(?im)^\s*(?:Power\s*On\s*Hours?|Часы\s+работы|Время\s+работы|Наработка)\s*:\s*(?<value>[\d\s.,]+?)\s*(?<unit>hours?|hrs?|h|час(?:а|ов|ы)?|ч|days?|d|дн(?:ей|я)?|years?|yrs?|лет|год(?:а|ов)?)?(?:\s|$)')
        $powerOnHours=$null
        if($powerOn.Success) {
            $amountText=$powerOn.Groups['value'].Value -replace '[^\d]',''
            if($amountText -match '^\d+$') {
                $amount=[long]$amountText
                $unit=$powerOn.Groups['unit'].Value
                $factor=if($unit -match '^(?:days?|d|дн)') {24} elseif($unit -match '^(?:years?|yrs?|лет|год)') {8760} else {1}
                $powerOnHours=[long]($amount*$factor)
            }
        }
        $type='Unknown'
        if($rotation -match 'SSD|Solid State' -or $interface -match 'NVM Express|NVMe'){$type='SSD'}
        elseif($rotation -match '\d+\s*RPM'){$type='HDD'}
        New-Object PSObject -Property @{Model=$model.Groups[1].Value.Trim();Status=$health.Groups[1].Value.Trim();Letters=$letters;MediaType=$type;TransferMode=$transfer;PowerOnHours=$powerOnHours}
    }
}
function Show-ServiceSummary {
    Section 'КРАТКАЯ СВОДКА'
    Get-SummaryLines | ForEach-Object {Write-SummaryLine $_}
}
function Write-SummaryLine([string]$Text) {
    if($Text -eq '05 | ИТОГ ДИАГНОСТИКИ'){Write-Host ("`n"+$Text) -ForegroundColor Green}
    elseif($Text -match '^0[1-6] \|'){Write-Host ("`n"+($Text -replace '^0[1-6] \| ','')) -ForegroundColor Cyan}
    elseif($Text -match '^! |^--- КРИТИЧНО'){Write-Host $Text -ForegroundColor Red}
    elseif($Text -match '^Внимание:'){Write-Host $Text -ForegroundColor Yellow}
    else {Write-Host $Text}
}
function Show-InitialSummary {
    foreach($line in (Get-SummaryLines)) {
        if($line -match '^05 \|'){break}
        Write-SummaryLine $line
    }
    Write-Host 'Процессы вне списка требуют проверки, но не обязательно вредоносны.' -ForegroundColor DarkGray
}
function Show-InitialSection([int]$Number) {
    $show=$false
    foreach($line in (Get-SummaryLines)) {
        if($line -match '^0[1-5] \|') {
            if($show){break}
            if($line.StartsWith(('0{0} |' -f $Number))){$show=$true}
        }
        if($show){Write-SummaryLine $line}
    }
}
function Show-FinalSummary {
    Write-Host "`n=== ИТОГ ДИАГНОСТИКИ ===" -ForegroundColor Green
    foreach($line in (Get-FinalSummaryLines)) {
        if($line -eq '06 | ФОНОВОЕ ОБСЛУЖИВАНИЕ'){break}
        Write-SummaryLine $line
    }
}
function Show-Scan {Get-ServiceSnapshot; Show-ServiceSummary}
function Get-SystemDiskMediaType {
    try {
        if(!(Get-Command Get-Partition -ErrorAction SilentlyContinue)){return 'Unknown'}
        $part=Get-Partition -DriveLetter $env:SystemDrive.Substring(0,1) -ErrorAction Stop
        $physical=@(Get-PhysicalDisk -ErrorAction Stop | Where-Object {[string]$_.DeviceId -eq [string]$part.DiskNumber})
        if($physical.Count -eq 1 -and @('SSD','HDD') -contains [string]$physical[0].MediaType){return [string]$physical[0].MediaType}
    } catch {}
    return 'Unknown'
}
