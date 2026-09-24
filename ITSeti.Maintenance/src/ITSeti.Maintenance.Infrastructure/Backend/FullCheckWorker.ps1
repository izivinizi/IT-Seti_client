param([string]$RunRoot,[string]$ToolsRoot,[switch]$SkipDiskTests,[switch]$LimitedMode,[switch]$HeadlessDiskSpd,[switch]$UserMode,[switch]$TrustedTools,[switch]$SkipResourceSampling,[switch]$SkipDiskBenchmark,[switch]$StartRepair)
$ErrorActionPreference='Stop'
$RunRoot=(Resolve-Path -LiteralPath $RunRoot).Path
$ScriptRoot=$RunRoot
$Days=7;$Samples=5
$script:Admin=([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$script:CompactOutput=$true
$script:DiskTestPasses=2
$script:QuietDiskTools=$true
$script:SkipWindowsUpdateChange=[bool]($SkipDiskTests -or $SkipDiskBenchmark)
$started=[DateTimeOffset]::Now.ToString('o')
$id=[guid]::NewGuid().ToString()
$logging=$false
function Write-Stage([string]$Text){[IO.File]::WriteAllText((Join-Path $RunRoot 'stage.txt'),$Text,[Text.Encoding]::UTF8)}
function Section([string]$Text){Write-Stage $Text}
function Parse-Speed($Value) {
    $number=0.0
    if([double]::TryParse(([string]$Value).Replace(',','.'),[Globalization.NumberStyles]::Float,[Globalization.CultureInfo]::InvariantCulture,[ref]$number) -and $number -gt 0){return $number}
    return $null
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
    if($s.ProcessUnavailable){$notes+=('Без доступа к исполняемому файлу процессов: '+$s.ProcessUnavailable)}
    if($s.EventUnavailable){$notes+=('Недоступно выборок журналов: '+$s.EventUnavailable)}
    $data=@{
        Id=$id;StartedAt=$started;ComputerName=$env:COMPUTERNAME;LastBootAt=$s.LastBootAt;WindowsEdition=$s.WindowsEdition;WindowsRelease=$s.WindowsRelease;WindowsBuild=$s.WindowsBuild
        CpuPercent=$(if($null -ne $s.Load){[double]$s.Load}else{-1})
        TotalMemoryBytes=[uint64]([double]$s.TotalRAM*1GB);AvailableMemoryBytes=[uint64]([double]$s.FreeRAM*1GB)
        Disks=@(foreach($v in $s.Volumes){@{Name=([string]$v.DeviceID+'\');TotalBytes=[long]$v.Size;FreeBytes=[long]$v.FreeSpace;VolumeId=[string]$v.VolumeSerialNumber}})
        Notes=$notes
        Full=@{
            CpuName=[string]$s.CPU;GpuName=[string]$s.GPU;MemoryType=[string]$s.MemoryType;Elevated=[bool]$script:Admin
            PhysicalDisks=@(foreach($d in $s.Disks){@{Model=[string]$d.FriendlyName;MediaType=[string]$d.MediaType;Health=[string]$d.HealthStatus}})
            SmartDisks=@(foreach($d in $s.Smart){@{Model=[string]$d.Model;Status=[string]$d.Status;Letters=[string]$d.Letters;MediaType=[string]$d.MediaType;TransferMode=[string]$d.TransferMode}})
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
        try {$script:Sampler=Start-Process powershell.exe -WindowStyle Hidden -WorkingDirectory $RunRoot -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$sampler`" -RunRoot `"$RunRoot`"" -RedirectStandardError (Join-Path $RunRoot 'resource-sampler.err.txt') -PassThru -ErrorAction Stop}
        catch {$script:SamplerFailure=$_.Exception.Message}
    }
    function Show-InitialSection([int]$Number) {
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
    if($script:SamplerFailure){$script:Snapshot.Notes+=@('Замер нагрузки: '+$script:SamplerFailure)}
    if($LimitedMode){$script:Snapshot.Notes+=@('Ограниченный режим: повышение прав недоступно. Часть процессов и событий может быть недоступна; SMART и тест скорости пропущены.')}
    if($UserMode){$script:Snapshot.Notes+=@('Проверка выполнена от текущего пользователя; защищённые системные сведения могут быть недоступны.')}
    try {$script:TopMemoryProcesses=@(Get-TopMemoryProcesses)} catch {$script:Snapshot.Notes+=@('Топ процессов по ОЗУ: '+$_.Exception.Message)}
    Save-Result 'partial.json' -Pending
    if($script:Sampler) {
        Write-Stage 'Подтверждение нагрузки CPU/ОЗУ и задержки диска за 30 секунд'
        if(!$script:Sampler.WaitForExit(45000)){
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
    if($HeadlessDiskSpd -and !$SkipDiskTests -and !$script:DiskFailure) {
        $diskWorker=Join-Path $RunRoot 'HeadlessDiskWorker.ps1'
        try {
            $diskArguments="-NoProfile -ExecutionPolicy Bypass -File `"$diskWorker`" -ScriptRoot `"$RunRoot`" -ToolsRoot `"$ToolsRoot`""
            if($SkipDiskBenchmark){$diskArguments+=' -SkipBenchmark'}
            $script:DiskWorker=Start-Process powershell.exe -WindowStyle Hidden -WorkingDirectory $RunRoot -ArgumentList $diskArguments -PassThru -ErrorAction Stop
            $script:DiskWorkerStarted=Get-Date
        } catch {$script:DiskFailure='Запуск DiskSpd: '+$_.Exception.Message}
    }
    if($script:DiskWorker){
        try {Complete-DiskToolsBackground} catch {$script:DiskFailure=$_.Exception.Message}
    }
    Save-Result 'result.json'
    Write-Stage 'Полная диагностика завершена'
    if($StartRepair -and $script:Admin -and !$SkipDiskTests -and !$LimitedMode) {
        $repair=Join-Path $RunRoot 'repair'
        try {
            New-Item -ItemType Directory -Path $repair -Force | Out-Null
            foreach($file in @('Repair.ps1','RepairWorker.ps1')){Copy-Item -LiteralPath (Join-Path $RunRoot $file) -Destination $repair -Force}
            $repairWorker=Join-Path $repair 'RepairWorker.ps1'
            Start-Process powershell.exe -WindowStyle Hidden -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$repairWorker`" -JobRoot `"$repair`"" -ErrorAction Stop | Out-Null
            [IO.File]::WriteAllText((Join-Path $repair 'started.txt'),[DateTimeOffset]::Now.ToString('o'),[Text.Encoding]::UTF8)
        } catch {
            [IO.File]::WriteAllText((Join-Path $repair 'status.txt'),('Запуск восстановления: '+$_.Exception.Message),[Text.Encoding]::UTF8)
        }
    }
} catch {
    [IO.File]::WriteAllText((Join-Path $RunRoot 'error.txt'),$_.Exception.Message,[Text.Encoding]::UTF8)
    Write-Stage ('Ошибка: '+$_.Exception.Message)
} finally {
    if($script:Sampler) {
        try {$script:Sampler.Refresh();if(!$script:Sampler.HasExited){$script:Sampler.Kill();[void]$script:Sampler.WaitForExit(5000)}} catch {}
    }
    if(Get-Command Exit-MaintenanceRun -ErrorAction SilentlyContinue){Exit-MaintenanceRun}
    if($logging){Stop-Transcript | Out-Null}
}
