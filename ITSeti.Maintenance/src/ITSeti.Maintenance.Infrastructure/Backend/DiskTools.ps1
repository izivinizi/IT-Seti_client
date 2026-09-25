function Find-Tool([string]$Relative) {
    $path = Join-Path $ToolsRoot $Relative
    if(!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Не найдено: $path. Укажите -ToolsRoot с корнем флешки." }
    return (Resolve-Path -LiteralPath $path).ProviderPath
}
function Initialize-NativeDiskMark {
    if(!('NativeDiskMark' -as [type])) {
        # The C# compiler ignores hidden source files; toolkit files on USB can be hidden.
        Add-Type -TypeDefinition ([IO.File]::ReadAllText((Join-Path $ScriptRoot 'NativeDiskMark.cs'),[Text.Encoding]::UTF8)) -ErrorAction Stop
    }
}
function Register-OwnedDiskTool($Process,[string]$Path,[switch]$Benchmark) {
    $script:OwnedDiskTools += @(New-Object PSObject -Property @{Process=$Process;Path=$Path;Started=$Process.StartTime;Benchmark=[bool]$Benchmark})
}
function Close-OwnedDiskTools {
    if($script:DiskWorker -and !$script:DiskWorker.HasExited){return}
    foreach($item in @($script:OwnedDiskTools)) {
        if(!$item){continue}
        try {
            $p=$item.Process
            $p.Refresh()
            if($p.HasExited){continue}
            if($p.StartTime -ne $item.Started -or $p.Path -ne $item.Path -or $p.SessionId -ne [Diagnostics.Process]::GetCurrentProcess().SessionId){continue}
            if($item.Benchmark) {
                Assert-DiskBackendIdle
                Initialize-NativeDiskMark
                $controls=@([NativeDiskMark]::Children($p.MainWindowHandle) | Where-Object {$_.Id -eq 1026 -and $_.Class -eq 'ComboBox'})
                if($controls.Count -ne 1 -or ![NativeDiskMark]::IsWindowEnabled($controls[0].Handle)){throw 'Тест ещё идёт или его состояние недоступно; окно оставлено открытым.'}
            }
            if($p.MainWindowHandle -eq [IntPtr]::Zero -or !$p.CloseMainWindow() -or !$p.WaitForExit(3000)){throw 'Программа не подтвердила закрытие; принудительное завершение не выполнялось.'}
        } catch {Write-Host ('Не закрыто '+[IO.Path]::GetFileName($item.Path)+': '+$_.Exception.Message) -ForegroundColor Yellow}
    }
}
function Start-DiskToolsBackground {
    if(!$script:Admin){throw 'Для автоматического теста диска нужны права администратора.'}
    $root64=[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($ScriptRoot))
    $tools64=[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($ToolsRoot))
    $command="`$r=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('$root64')); `$t=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('$tools64')); & ([scriptblock]::Create([IO.File]::ReadAllText((Join-Path `$r 'DiskWorker.ps1'),[Text.Encoding]::UTF8))) -ScriptRoot `$r -ToolsRoot `$t"
    if($script:DiskTestPasses -eq 2){$command+=' -Passes 2'}
    if($script:QuietDiskTools){$command+=' -Quiet'}
    $encoded=[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
    $script:DiskWorker=Start-Process powershell.exe -WindowStyle Hidden -WorkingDirectory $ScriptRoot -ArgumentList "-NoProfile -ExecutionPolicy Bypass -EncodedCommand $encoded" -PassThru -ErrorAction Stop
    $script:DiskWorkerStarted=Get-Date
    Write-Host 'Дисковые утилиты запускаются отдельно. Сбор сводки продолжается.' -ForegroundColor DarkGray
}
function Complete-DiskToolsBackground {
    if(!$script:DiskWorker){return}
    if(!$script:DiskWorker.HasExited){Write-Host 'Основная проверка готова. Ожидается результат дискового теста...' -ForegroundColor DarkGray}
    $timeout=300; if($script:DiskTestPasses -eq 2){$timeout=480}
    $remaining=[math]::Max(0,$timeout-((Get-Date)-$script:DiskWorkerStarted).TotalSeconds)
    if(!$script:DiskWorker.WaitForExit([int]($remaining*1000))){throw 'Дисковые утилиты ещё работают. Очистка не запущена; подробности в disk-worker.log. Тест не прерван.'}
    $file=Join-Path $ScriptRoot 'disk-result.xml'
    if(!(Test-Path -LiteralPath $file)){throw ('Процесс дисковых утилит завершился без результата, код '+$script:DiskWorker.ExitCode+'. Подробности: disk-worker.log.')}
    $data=Import-Clixml -LiteralPath $file
    $script:DiskResult=$data.Result
    $script:DiskFailure=$data.Failure
    $script:Snapshot.Smart=@($data.Smart)
    $script:Snapshot.Notes+=@($data.Notes)
    foreach($item in $data.Owned){
        try {
            $p=Get-Process -Id $item.Id -ErrorAction Stop
            if($p.Path -eq $item.Path -and $p.StartTime -eq $item.Started){Register-OwnedDiskTool $p $item.Path -Benchmark:$item.Benchmark}
        } catch {}
    }
}
function Get-LocalDiskMarkProcesses {
    $session=[Diagnostics.Process]::GetCurrentProcess().SessionId
    @(Get-Process DiskMark64,DiskMark64A -ErrorAction SilentlyContinue | Where-Object {$_.SessionId -eq $session})
}
function Assert-DiskBackendIdle {
    if(Get-Process DiskSpd* -ErrorAction SilentlyContinue){throw 'Уже работает тестовый движок DiskSpd. Дождитесь окончания теста; новый тест и очистка пока не запускаются.'}
}
function Clear-PreviousDiskMark {
    Assert-DiskBackendIdle
    Initialize-NativeDiskMark
    foreach($p in @(Get-LocalDiskMarkProcesses)) {
        $path=$null
        try {$path=$p.Path} catch {}
        $ours=$false
        foreach($kind in @('Sessions','ToolCache')) {
            $base=[IO.Path]::GetFullPath((Join-Path $env:ProgramData ('ServiceMaintenance\'+$kind)))+'\'
            if($path -and $path.StartsWith($base,[StringComparison]::OrdinalIgnoreCase) -and $path -match '\\CrystalDiskMark9\\DiskMark64A?\.exe$'){$ours=$true}
        }
        if(!$ours){throw ('Уже открыт сторонний CrystalDiskMark (PID '+$p.Id+'). Закройте его перед автотестом.')}
        if($p.MainWindowHandle -ne [IntPtr]::Zero) {
            $control=@([NativeDiskMark]::Children($p.MainWindowHandle) | Where-Object {$_.Id -eq 1026 -and $_.Class -eq 'ComboBox'})
            if($control.Count -ne 1 -or ![NativeDiskMark]::IsWindowEnabled($control[0].Handle)){throw ('CrystalDiskMark занят или недоступен (PID '+$p.Id+'). Текущий тест не прерывается.')}
            if(!$p.CloseMainWindow() -or !$p.WaitForExit(5000)){throw ('Не удалось закрыть прошлое окно CrystalDiskMark (PID '+$p.Id+').')}
        } else {
            if(((Get-Date)-$p.StartTime).TotalSeconds -lt 30){throw 'Предыдущий CrystalDiskMark ещё запускается. Повторите позже.'}
            # Retire only an old toolkit process without a window or active test engine.
            Assert-DiskBackendIdle
            Write-Host ('Закрывается оставшийся процесс комплекта без окна: PID '+$p.Id)
            Stop-Process -Id $p.Id -ErrorAction Stop
            if(!$p.WaitForExit(5000)){throw 'Предыдущий процесс CrystalDiskMark не завершился.'}
        }
    }
}
function Start-DiskTools([switch]$NoWait) {
    $script:DiskResult=$null
    Section 'Дисковые утилиты'
    if(!$script:Admin) { throw 'Запустите Start.cmd: управление тестом требует прав администратора.' }
    if([IntPtr]::Size -lt 8 -and !$env:PROCESSOR_ARCHITEW6432) { throw 'В комплекте указаны 64-битные дисковые утилиты. На 32-битной Windows доступна сводка, но нужны отдельные x86-утилиты.' }
    $info = Find-Tool 'CrystalDiskInfo9_6_3_Portable\DiskInfo64.exe'
    if($script:Snapshot -and (Get-Command ConvertFrom-CdiReport -ErrorAction SilentlyContinue)) {
        try {
            $report=Join-Path (Split-Path $info) 'DiskInfo.txt'
            $startedAt=Get-Date
            $exportStyle='Normal';if($script:QuietDiskTools){$exportStyle='Hidden'}
            $export=Start-Process -FilePath $info -WorkingDirectory (Split-Path $info) -ArgumentList '/CopyExit' -WindowStyle $exportStyle -PassThru
            if(!$export.WaitForExit(30000)){throw 'Экспорт SMART не завершился за 30 секунд.'}
            if(!(Test-Path -LiteralPath $report) -or (Get-Item -LiteralPath $report).LastWriteTime -lt $startedAt.AddSeconds(-2)){throw 'Свежий отчёт SMART не получен.'}
            $script:Snapshot.Smart=@(ConvertFrom-CdiReport ([IO.File]::ReadAllText($report)))
            if(!$script:Snapshot.Smart.Count){throw 'Формат отчёта SMART не распознан.'}
        } catch {$script:Snapshot.Notes += ('SMART: '+$_.Exception.Message)}
    }
    $infoSession=[Diagnostics.Process]::GetCurrentProcess().SessionId
    $visibleInfo=@(Get-Process DiskInfo64 -ErrorAction SilentlyContinue | Where-Object {$_.SessionId -eq $infoSession -and $_.MainWindowHandle -ne [IntPtr]::Zero})
    if(!$visibleInfo.Count -and !$script:QuietDiskTools) {
        $infoProcess=Start-Process -FilePath $info -WorkingDirectory (Split-Path $info) -PassThru -ErrorAction Stop
        Register-OwnedDiskTool $infoProcess $info
    }
    $relative='CrystalDiskMark9\DiskMark64.exe'
    if(!(Test-Path -LiteralPath (Join-Path $ToolsRoot $relative))){$relative='CrystalDiskMark9\DiskMark64A.exe'}
    $mark = Find-Tool $relative
    $processName=[IO.Path]::GetFileNameWithoutExtension($mark)
    $disk = Get-WmiObject Win32_LogicalDisk -Filter "DeviceID='$env:SystemDrive'"
    if(!$disk -or $disk.DriveType -ne 3 -or $disk.FreeSpace -lt 3GB) { throw 'Для теста требуется локальный системный диск с минимум 3 ГБ свободного места.' }
    # Probe only a newly created file, automatically removed when its handle is closed.
    $probePath = Join-Path ($env:SystemDrive+'\') ('ServiceDiskProbe-'+[guid]::NewGuid().ToString('N')+'.tmp')
    try {
        $probe = New-Object IO.FileStream -ArgumentList @($probePath,[IO.FileMode]::CreateNew,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None,4096,[IO.FileOptions]::DeleteOnClose)
        try { $probe.WriteByte(0); $probe.Flush() } finally { $probe.Dispose() }
    } catch { throw "Нет доступа для тестовой записи на $env:SystemDrive. Тест не запущен: $($_.Exception.Message)" }
    Clear-PreviousDiskMark
    $style='Normal';if($script:QuietDiskTools){$style='Minimized'}
    $launched=Start-Process -FilePath $mark -WorkingDirectory (Split-Path $mark) -WindowStyle $style -PassThru -ErrorAction Stop
    Register-OwnedDiskTool $launched $mark -Benchmark
    Initialize-NativeDiskMark
    $p = $null
    for($i=0;$i -lt 30;$i++) {
        $launched.Refresh()
        if($launched.HasExited){throw ('Новый CrystalDiskMark завершился до открытия окна, код '+$launched.ExitCode+'. Проверьте запреты запуска и другие экземпляры программы.')}
        if($launched.MainWindowHandle -ne [IntPtr]::Zero){$p=$launched; break}
        Start-Sleep -Seconds 1
    }
    if(!$p) { throw 'Окно CrystalDiskMark не найдено за 30 секунд.' }
    if($p.Path -ne $mark -or $p.MainWindowTitle -notmatch 'CrystalDiskMark 9\.0\.[23](\D|$)') { throw 'Автотест поддерживает CrystalDiskMark 9.0.2 / 9.0.3 из папки комплекта.' }
    $h = $p.MainWindowHandle
    $controls = [NativeDiskMark]::Children($h)
    $first = @($controls | Where-Object { $_.Id -eq 1004 -and $_.Class -eq 'Button' -and $_.Text -match '^SEQ' })
    if($first.Count -ne 1) { throw 'Первая кнопка SEQ не найдена. Тест не запущен.' }
    # IDs verified against the installed 9.0.3 window; validate item labels before changing selection.
    $passes=1;if($script:DiskTestPasses -eq 2){$passes=2}
    foreach($setting in @(@{Id=1026;Label=('^'+$passes+'$')},@{Id=1028;Label='^1GiB$'},@{Id=1027;Label=('^'+[regex]::Escape($env:SystemDrive)+'\s')},@{Id=1025;Label='^MB/s$'})) {
        $combo = @($controls | Where-Object { $_.Id -eq $setting.Id -and $_.Class -eq 'ComboBox' })
        if($combo.Count -ne 1 -or ![NativeDiskMark]::IsWindowEnabled($combo[0].Handle)) { throw 'Настройки недоступны: возможно, тест уже идёт.' }
        $ch = $combo[0].Handle
        $count = [int][NativeDiskMark]::SendMessage($ch,0x146,0,0)
        if($count -le 0) { throw 'Нет доступа к управлению CrystalDiskMark. Запустите весь комплект через Start.cmd с повышением прав; если запуск запрещён политикой организации, требуется её администратор.' }
        $matchesFound = @(for($j=0;$j -lt $count;$j++) { if([NativeDiskMark]::Item($ch,$j) -match $setting.Label) {$j} })
        if($matchesFound.Count -ne 1) { throw "Не удалось однозначно выбрать настройку $($setting.Id)." }
        [NativeDiskMark]::Select($h,$ch,$matchesFound[0])
    }
    Write-Host "Первый SEQ-тест: $env:SystemDrive, проходов: $passes, 1 GiB, чтение и запись."
    if(![NativeDiskMark]::PostMessage($h,0x111,1004,$first[0].Handle)) { throw 'Не удалось отправить команду первого теста.' }
    $script:PendingDiskTest=@{Process=$p;Handle=$h;Started=$false}
    Complete-DiskTest -LaunchOnly:$NoWait
}
function Complete-DiskTest([switch]$LaunchOnly) {
    if(!$script:PendingDiskTest){return}
    $p=$script:PendingDiskTest.Process
    $h=$script:PendingDiskTest.Handle
    $started = $script:PendingDiskTest.Started
    $completed = $false
    $limitSeconds=180;if($script:DiskTestPasses -eq 2){$limitSeconds=360}
    for($i=0;$i -lt $limitSeconds;$i++) {
        Start-Sleep -Seconds 1
        $p.Refresh()
        if($p.HasExited) { throw 'Окно теста закрыто до получения результата.' }
        $popup = [NativeDiskMark]::GetLastActivePopup($h)
        if($popup -ne $h -and $popup -ne [IntPtr]::Zero) {
            $popupText = ([NativeDiskMark]::Children($popup) | Where-Object { $_.Class -eq 'Static' -and $_.Text } | ForEach-Object { $_.Text }) -join ' '
            throw "CrystalDiskMark открыл диалог: $popupText. Тест не подтверждён; проверьте окно программы."
        }
        $current = [NativeDiskMark]::Children($h)
        $countControl = $current | Where-Object { $_.Id -eq 1026 }
        $busy = ![NativeDiskMark]::IsWindowEnabled($countControl.Handle)
        if($busy) {
            $started=$true
            $script:PendingDiskTest.Started=$true
            if($LaunchOnly){return}
        }
        if($started -and !$busy) {
            $read = ($current | Where-Object { $_.Id -eq 1009 }).Text
            $write = ($current | Where-Object { $_.Id -eq 1014 }).Text
            $media='Unknown'
            if(Get-Command Get-SystemDiskMediaType -ErrorAction SilentlyContinue){$media=Get-SystemDiskMediaType}
            if($media -eq 'Unknown' -and $script:Snapshot) {
                $match=@($script:Snapshot.Smart | Where-Object {($_.Letters -split '\s+') -contains $env:SystemDrive})
                if($match.Count -eq 1){$media=$match[0].MediaType}
            }
            $systemSmart=@($script:Snapshot.Smart | Where-Object {($_.Letters -split '[, ]+') -contains $env:SystemDrive})
            $isNvme=$media -eq 'SSD' -and (($systemSmart | Where-Object {$_.TransferMode -match 'PCIe|NVMe|NVM Express'}).Count -gt 0)
            $script:DiskResult=@{Read=$read;Write=$write;MediaType=$media;IsNvme=$isNvme;Passes=$(if($script:DiskTestPasses -eq 2){2}else{1});Drive=$env:SystemDrive}
            if(!$script:CompactOutput){Write-Host "SEQ: чтение $read MB/s; запись $write MB/s. При остановке вручную результат может быть неполным."}
            $script:PendingDiskTest=$null
            $completed=$true; break
        }
        if(!$started -and $i -ge 10) { throw 'Не удалось подтвердить начало теста. Проверьте окно CrystalDiskMark.' }
    }
    if(!$completed) { throw ('Ожидание теста превысило '+$limitSeconds+' секунд. Он может продолжаться в окне CrystalDiskMark.') }
    if(!$script:CompactOutput){Write-Host 'Последовательный тест не характеризует полностью отзывчивость системы и не проверяет всю поверхность диска.'}
}
function Assert-DiskTestIdle {
    if($script:DiskWorker -and !$script:DiskWorker.HasExited){throw 'Дисковые утилиты ещё работают; очистка и восстановление пока не запускаются.'}
    Assert-DiskBackendIdle
    $instances = @(Get-LocalDiskMarkProcesses)
    if(!$instances.Count) { return }
    Initialize-NativeDiskMark
    foreach($p in $instances) {
        $control = @([NativeDiskMark]::Children($p.MainWindowHandle) | Where-Object { $_.Id -eq 1026 -and $_.Class -eq 'ComboBox' })
        if($control.Count -ne 1 -or ![NativeDiskMark]::IsWindowEnabled($control[0].Handle)) { throw 'Дождитесь завершения теста CrystalDiskMark или закройте его перед обслуживанием.' }
    }
}
