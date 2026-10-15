param([string]$RunRoot,[string]$ToolsRoot,[switch]$SkipDiskTests,[switch]$LimitedMode,[switch]$HeadlessDiskSpd,[switch]$InteractiveDiskMark,[switch]$UserMode,[switch]$TrustedTools,[switch]$SkipResourceSampling,[switch]$SkipDiskBenchmark,[switch]$StartRepair,[switch]$Scheduled)
$ErrorActionPreference='Stop'
$CpuTemperatureFile=$env:ITSETI_CPU_TEMPERATURE_FILE
$RunRoot=(Resolve-Path -LiteralPath $RunRoot).Path
$ScriptRoot=$RunRoot
. (Join-Path $ScriptRoot 'HWiNFO.ps1')
$Days=7;$Samples=5
$script:Admin=([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$script:CompactOutput=$true
$script:DiskTestPasses=2
$script:QuietDiskTools=$true
$script:InteractiveDiskMark=[bool]$InteractiveDiskMark
$script:ForbidInteractiveDiskTools=!$InteractiveDiskMark
$started=[DateTimeOffset]::Now.ToString('o')
$id=[guid]::NewGuid().ToString()
$logging=$false
$script:DiskBenchmarkReadyFile=Join-Path $RunRoot 'benchmark-ready.signal'
function Write-Stage([string]$Text){[IO.File]::WriteAllText((Join-Path $RunRoot 'stage.txt'),$Text,[Text.Encoding]::UTF8)}
function Publish-SmartProgress {
    if($script:SmartProgressPublished -or !$script:Snapshot){return}
    $path=Join-Path $RunRoot 'smart-result.xml'
    if(!(Test-Path -LiteralPath $path -PathType Leaf)){return}
    try {
        $smart=Import-Clixml -LiteralPath $path
        if(@($smart.Smart).Count -eq 0){return}
        $script:Snapshot.Smart=@($smart.Smart)
        Save-Result 'partial.json' -Pending
        $script:SmartProgressPublished=$true
    } catch [IO.IOException] { }
}
function Assert-CheckNotCancelled {
    $request=Join-Path $RunRoot 'cancel-request.txt'
    if(!(Test-Path -LiteralPath $request -PathType Leaf)){return}
    $value=[IO.File]::ReadAllText($request).Trim()
    if($value -eq 'cancel'){throw 'Проверка остановлена пользователем.'}
}
function Section([string]$Text){Write-Stage $Text}
function Parse-Speed($Value) {
    $number=0.0
    if([double]::TryParse(([string]$Value).Replace(',','.'),[Globalization.NumberStyles]::Float,[Globalization.CultureInfo]::InvariantCulture,[ref]$number) -and $number -gt 0){return $number}
    return $null
}
function Get-ActiveUptimeHoursFallback($OperatingSystem) {
    try {
        if(!$OperatingSystem -or !$OperatingSystem.LastBootUpTime){return $null}
        $lastBoot=[Management.ManagementDateTimeConverter]::ToDateTime([string]$OperatingSystem.LastBootUpTime)
        $hours=([DateTime]::Now-$lastBoot).TotalHours
        if([double]::IsNaN($hours) -or [double]::IsInfinity($hours) -or $hours -lt 0 -or $hours -gt 87600){return $null}
        return [math]::Round($hours,1)
    } catch {return $null}
}
function Get-TopMemoryProcesses {
    $known=@{chrome='Google Chrome';msedge='Microsoft Edge';firefox='Mozilla Firefox';'1cv8'='1С';'1cv8c'='1С';MsMpEng='Защита Windows';explorer='Проводник Windows';svchost='Службы Windows';Teams='Microsoft Teams';OUTLOOK='Microsoft Outlook';WINWORD='Microsoft Word';EXCEL='Microsoft Excel'}
    $groups=@(Get-Process -ErrorAction Stop | Group-Object ProcessName | ForEach-Object {
        New-Object PSObject -Property @{Name=$_.Name;Processes=$_.Group;Bytes=($_.Group | Measure-Object WorkingSet64 -Sum).Sum}
    } | Sort-Object Bytes -Descending | Select-Object -First 3)
    foreach($group in $groups) {
        $name='';$publisher='';$path=''
        foreach($process in $group.Processes) {
            try {$path=$process.Path} catch {}
            if($path){break}
        }
        if($known.ContainsKey([string]$group.Name)){$name=$known[[string]$group.Name]}
        if($path) {
            try {
                $version=[Diagnostics.FileVersionInfo]::GetVersionInfo($path)
                if(!$name -and $version.ProductName){$name=$version.ProductName.Trim()}
                elseif(!$name -and $version.FileDescription){$name=$version.FileDescription.Trim()}
                $publisher=[string]$version.CompanyName
            } catch {}
        }
        if(!$name){$name='Программа без названия'}
        @{DisplayName=$name;ProcessName=[string]$group.Name;WorkingSetBytes=[long]$group.Bytes;Publisher=$publisher}
    }
}
function Save-Result([string]$Name,[switch]$Pending) {
    $s=$script:Snapshot
    $read=Parse-Speed $script:DiskResult.Read
    $write=Parse-Speed $script:DiskResult.Write
    $state='Completed';$failure=[string]$script:DiskFailure
    if($Pending){$state='Running'}
    elseif($SkipDiskTests){$state='Skipped';$failure=$(if($LimitedMode){'Автотест диска и SMART не запускались без прав администратора.'}else{'Тестовая проверка без запуска дисковых утилит.'})}
    elseif($SkipDiskBenchmark){$state='Skipped';$failure='Быстрая проверка: измерение скорости не запускалось.'}
    elseif($failure -or $null -eq $read -or $null -eq $write){$state='Failed';if(!$failure){$failure='Завершённый результат чтения/записи не получен.'}}
    $events=@(foreach($e in $s.Events){@{Log=[string]$e.LogName;Provider=[string]$e.ProviderName;Id=[int]$e.Id;Level=[int]$e.Level;RecordId=[long]$e.RecordId;Time=$e.TimeCreated.ToString('o');Message=$(if($e.Message){[string]$e.Message}else{'Описание не получено для этого события.'})}})
    $notes=@($s.Notes | Where-Object {$_})
    if($s.EventUnavailable){$notes+=('Недоступно выборок журналов: '+$s.EventUnavailable)}
    $data=@{
        Id=$id;KindLabel=$(if($Scheduled){'Плановая проверка'}else{'Проверка'});Trigger=[string]$env:ITSETI_CHECK_TRIGGER;WindowsSessionId=[Diagnostics.Process]::GetCurrentProcess().SessionId;RequestedByWindowsSessionId=$(if($env:ITSETI_REQUESTED_SESSION_ID -match '^\d+$'){[int]$env:ITSETI_REQUESTED_SESSION_ID}else{$null});StartedAt=$started;ComputerName=$env:COMPUTERNAME;SystemDrive=$env:SystemDrive;LastBootAt=$s.LastBootAt;ActiveUptimeHours=$s.ActiveUptimeHours;WindowsEdition=$s.WindowsEdition;WindowsRelease=$s.WindowsRelease;WindowsBuild=$s.WindowsBuild;CpuTemperatureC=$(if($null -ne $s.CpuTemperatureC){[double]$s.CpuTemperatureC}else{$null});CpuTemperatureStatus=[string]$s.CpuTemperatureStatus
        CpuPercent=$(if($null -ne $s.Load){[double]$s.Load}else{-1})
        TotalMemoryBytes=[uint64]([double]$s.TotalRAM*1GB);AvailableMemoryBytes=[uint64]([double]$s.FreeRAM*1GB)
        NetworkAdapters=@(foreach($adapter in @($s.NetworkAdapters)){@{Name=[string]$adapter.Name;Type=[string]$adapter.Type;Status=[string]$adapter.Status;LinkSpeed=[string]$adapter.LinkSpeed;MacAddress=[string]$adapter.MacAddress;Addresses=[string]$adapter.Addresses}})
        HWiNFO=$(if($s.HwInfo){@{
            Source=[string]$s.HwInfo.Source;ToolVersion=[string]$s.HwInfo.ToolVersion;CapturedAtUtc=$s.HwInfo.CapturedAtUtc
            CpuName=[string]$s.HwInfo.CpuName;Motherboard=[string]$s.HwInfo.Motherboard;BiosVersion=[string]$s.HwInfo.BiosVersion;MemoryType=[string]$s.HwInfo.MemoryType
            CpuTemperatureC=$(if($null -ne $s.HwInfo.CpuTemperatureC){[double]$s.HwInfo.CpuTemperatureC}else{$null})
            VrmTemperatureC=$(if($null -ne $s.HwInfo.VrmTemperatureC){[double]$s.HwInfo.VrmTemperatureC}else{$null})
            PchTemperatureC=$(if($null -ne $s.HwInfo.PchTemperatureC){[double]$s.HwInfo.PchTemperatureC}else{$null})
            Rail12V=$(if($null -ne $s.HwInfo.Rail12V){[double]$s.HwInfo.Rail12V}else{$null})
            Rail5V=$(if($null -ne $s.HwInfo.Rail5V){[double]$s.HwInfo.Rail5V}else{$null})
            CpuVoltageV=$(if($null -ne $s.HwInfo.CpuVoltageV){[double]$s.HwInfo.CpuVoltageV}else{$null})
            CpuLoadPercent=$(if($null -ne $s.HwInfo.CpuLoadPercent){[double]$s.HwInfo.CpuLoadPercent}else{$null})
            Sensors=@(foreach($sensor in @($s.HwInfo.Sensors)){@{Name=[string]$sensor.Name;Value=[double]$sensor.Value;Unit=[string]$sensor.Unit;Category=[string]$sensor.Category;Status=[string]$sensor.Status;IsWarning=[bool]$sensor.IsWarning;IsCritical=[bool]$sensor.IsCritical}})
        }}else{$null})
        Disks=@(foreach($v in $s.Volumes){@{Name=([string]$v.DeviceID+'\');TotalBytes=[long]$v.Size;FreeBytes=[long]$v.FreeSpace;VolumeId=[string]$v.VolumeSerialNumber}})
        Notes=$notes
        Full=@{
            CpuName=[string]$s.CPU;GpuName=[string]$s.GPU;MemoryType=[string]$s.MemoryType;CpuTemperatureC=$(if($null -ne $s.CpuTemperatureC){[double]$s.CpuTemperatureC}else{$null});Elevated=[bool]$script:Admin
            PhysicalDisks=@(foreach($d in $s.Disks){@{Model=[string]$d.FriendlyName;MediaType=[string]$d.MediaType;Health=[string]$d.HealthStatus;PowerOnHours=$(if($null -ne $d.PowerOnHours){[long]$d.PowerOnHours}else{$null})}})
            SmartDisks=@(foreach($d in $s.Smart){@{Model=[string]$d.Model;Status=[string]$d.Status;Letters=[string]$d.Letters;MediaType=[string]$d.MediaType;TransferMode=[string]$d.TransferMode;PowerOnHours=$(if($null -ne $d.PowerOnHours){[long]$d.PowerOnHours}else{$null});SmartWarnings=[string]$d.SmartWarnings}})
            Processes=@(foreach($p in $s.Processes){@{Name=[string]$p.Name;Description=[string]$p.Description;Publisher=$(if($p.Signature -eq 'Valid'){$p.Signer}else{[string]$p.Company+' (из файла)'});Signature=[string]$p.Signature;Path=[string]$p.Path}})
            TopMemoryProcesses=@($script:TopMemoryProcesses)
            ResourceSampling=$script:ResourceSampling
            Events=$events;ProcessUnavailable=[int]$s.ProcessUnavailable;EventUnavailable=[int]$s.EventUnavailable;EventLimited=[bool]$s.EventLimited
            Benchmark=@{Drive=$env:SystemDrive;MediaType=[string]$script:DiskResult.MediaType;IsNvme=[bool]$script:DiskResult.IsNvme;Read=$read;Write=$write;Passes=2;State=$state;Error=$failure;Engine=$(if($HeadlessDiskSpd){'DiskSpd'}else{'CrystalDiskMark'})}
            ReportDirectory=$RunRoot
        }
    }
    $temporary=Join-Path $RunRoot ($Name+'.pending')
    $data | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $temporary -Encoding UTF8
    Move-Item -LiteralPath $temporary -Destination (Join-Path $RunRoot $Name) -Force
}
try {
    Start-Transcript -Path (Join-Path $RunRoot 'full-check.log') -Force | Out-Null
    $logging=$true
    Assert-CheckNotCancelled
    foreach($file in @('Summary.ps1','DiskTools.ps1','Runtime.ps1')){. ([scriptblock]::Create([IO.File]::ReadAllText((Join-Path $RunRoot $file),[Text.Encoding]::UTF8)))}
    if(!(Enter-MaintenanceRun -AllowBackgroundMaintenance)){throw 'Повторная диагностика уже выполняется.'}
    Write-Stage 'Подготовка локальных дисковых утилит'
    if(!$SkipDiskTests) {
        if(!$ToolsRoot){$script:DiskFailure='Утилиты не найдены. Подключите флешку с CrystalDiskInfo и CrystalDiskMark или выберите папку утилит.'}
        elseif(!$TrustedTools) {
            try {$ToolsRoot=Get-LocalToolCache $ToolsRoot}
            catch {$script:DiskFailure='Копирование утилит: '+$_.Exception.Message}
        }
    }
    if($HeadlessDiskSpd -and !$SkipResourceSampling) {
        $sampler=Join-Path $RunRoot 'ResourceSampler.ps1'
        try {$script:Sampler=Start-Process (Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe') -WindowStyle Hidden -WorkingDirectory (Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0') -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$sampler`" -RunRoot `"$RunRoot`"" -RedirectStandardError (Join-Path $RunRoot 'resource-sampler.err.txt') -PassThru -ErrorAction Stop}
        catch {$script:SamplerFailure=$_.Exception.Message}
    }
    if($HeadlessDiskSpd -and !$SkipDiskTests -and !$script:DiskFailure) {
        $diskWorker=Join-Path $RunRoot 'HeadlessDiskWorker.ps1'
        try {
            $diskArguments='-NoProfile -ExecutionPolicy Bypass -File "'+$diskWorker+'" -ScriptRoot "'+$RunRoot+'" -ToolsRoot "'+$ToolsRoot+'"'
            if($SkipDiskBenchmark){$diskArguments+=' -SkipBenchmark'}
            if($UserMode){$diskArguments+=' -UserMode'}
            $script:DiskWorker=Start-Process (Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe') -WindowStyle Hidden -WorkingDirectory (Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0') -ArgumentList $diskArguments -PassThru -ErrorAction Stop
            $script:DiskWorkerStarted=Get-Date
        } catch {$script:DiskFailure='Запуск SMART/DiskSpd: '+$_.Exception.Message}
    }
    function Show-InitialSection([int]$Number) {
        Publish-SmartProgress
        switch($Number){
            1 {
                Write-Stage 'CPU/ОЗУ измерены. Проверка дисков и процессов'
            }
            3 {Write-Stage 'Процессы проверены. Сбор важных событий Windows'}
            4 {Write-Stage 'Основная сводка готова. SMART и SEQ: два прохода'}
        }
    }
    Write-Stage 'Замер CPU, ОЗУ и оборудования до дискового теста'
    Get-ServiceSnapshot -Live -StartDiskTest:(!$HeadlessDiskSpd -and !$SkipDiskTests -and !$script:DiskFailure)
    Assert-CheckNotCancelled
    if($CpuTemperatureFile -and (Test-Path -LiteralPath $CpuTemperatureFile)) {
        try {
            $sensor=Get-Content -LiteralPath $CpuTemperatureFile -Raw -Encoding UTF8 | ConvertFrom-Json
            if($null -ne $sensor.TemperatureC -and [double]$sensor.TemperatureC -gt 0 -and [double]$sensor.TemperatureC -le 120){$script:Snapshot.CpuTemperatureC=[double]$sensor.TemperatureC}
            if($sensor.Status){$script:Snapshot.CpuTemperatureStatus=[string]$sensor.Status}
        } catch {$script:Snapshot.CpuTemperatureStatus='Не удалось прочитать результат датчика: '+$_.Exception.Message}
    }
    if(!$script:Snapshot.CpuTemperatureStatus) {$script:Snapshot.CpuTemperatureStatus=if($null -ne $script:Snapshot.CpuTemperatureC){'Температура получена из Windows/WMI'}else{'Источник температуры не вернул данных'}}
    if($script:SamplerFailure){$script:Snapshot.Notes+=@('Замер нагрузки: '+$script:SamplerFailure)}
    if($LimitedMode){$script:Snapshot.Notes+=@('Ограниченный режим: повышение прав недоступно. Часть процессов и событий может быть недоступна; SMART и тест скорости пропущены.')}
    if($UserMode -and !$script:Admin){$script:Snapshot.Notes+=@('Проверка выполнена от текущего пользователя; защищённые системные сведения могут быть недоступны.')}
    try {$script:TopMemoryProcesses=@(Get-TopMemoryProcesses)} catch {$script:Snapshot.Notes+=@('Топ процессов по ОЗУ: '+$_.Exception.Message)}
    Save-Result 'partial.json' -Pending
    Publish-SmartProgress
    if($script:Sampler) {
        Write-Stage 'Подтверждение нагрузки CPU/ОЗУ за 30 секунд; DiskSpd измерит задержку отдельно'
        $samplerDeadline=[DateTime]::UtcNow.AddSeconds(45)
        while(!$script:Sampler.WaitForExit(500) -and [DateTime]::UtcNow -lt $samplerDeadline){Assert-CheckNotCancelled;Publish-SmartProgress}
        if(!$script:Sampler.HasExited){
            try {$script:Sampler.Kill();[void]$script:Sampler.WaitForExit(5000)} catch {}
            $script:Snapshot.Notes+=@('Замер нагрузки не завершился за 45 секунд; зависший процесс остановлен.')
        }
        else {
            $sampleFile=Join-Path $RunRoot 'resource-sample.json'
            try {
                if(!(Test-Path -LiteralPath $sampleFile)){
                    $detail=Get-Content -LiteralPath (Join-Path $RunRoot 'resource-sampler.err.txt') -Raw -ErrorAction SilentlyContinue
                    throw "Процесс замера не сохранил результат (код $($script:Sampler.ExitCode)). $detail"
                }
                $script:ResourceSampling=Get-Content -LiteralPath $sampleFile -Raw -Encoding UTF8 | ConvertFrom-Json
                if($script:ResourceSampling.Error){$script:Snapshot.Notes+=@('Замер нагрузки: '+$script:ResourceSampling.Error)}
            } catch {$script:Snapshot.Notes+=@('Замер нагрузки: '+$_.Exception.Message)}
        }
    }
    [IO.File]::WriteAllText($script:DiskBenchmarkReadyFile,'ready',[Text.Encoding]::ASCII)
    Assert-CheckNotCancelled
    if($script:DiskWorker){
        try {Complete-DiskToolsBackground} catch {$script:DiskFailure=$_.Exception.Message}
    }
    Assert-CheckNotCancelled
    Save-Result 'result.json'
    Write-Stage 'Полная диагностика завершена'
} catch {
    if($_.Exception.Message -eq 'Проверка остановлена пользователем.'){
        $script:CheckCancelled=$true
        Write-Stage 'Проверка остановлена'
    } else {
        [IO.File]::WriteAllText((Join-Path $RunRoot 'error.txt'),$_.Exception.Message,[Text.Encoding]::UTF8)
        Write-Stage ('Ошибка: '+$_.Exception.Message)
    }
} finally {
    if($script:DiskWorker -and !$script:DiskWorker.HasExited){
        try {if(!$script:DiskWorker.WaitForExit(3000)){$script:DiskWorker.Kill()}} catch {}
    }
    if($script:Sampler) {
        try {$script:Sampler.Refresh();if(!$script:Sampler.HasExited){$script:Sampler.Kill();[void]$script:Sampler.WaitForExit(5000)}} catch {}
    }
    if(Get-Command Exit-MaintenanceRun -ErrorAction SilentlyContinue){try {Exit-MaintenanceRun} catch {}}
    if($logging){try {Stop-Transcript | Out-Null} catch {}}
    if($script:CheckCancelled){[IO.File]::WriteAllText((Join-Path $RunRoot 'cancelled.txt'),'cancelled',[Text.Encoding]::ASCII)}
}
