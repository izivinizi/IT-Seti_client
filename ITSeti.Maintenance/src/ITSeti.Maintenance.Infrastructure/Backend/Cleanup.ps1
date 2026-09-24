$script:UserCleanupNames = @('Recycle Bin','Temporary Files','Thumbnail Cache','Internet Cache Files','Internet Cache','D3D Shader Cache')
$script:CleanupNames = $script:UserCleanupNames + @('Delivery Optimization Files','Update Cleanup','Device Driver Packages')
function New-CleanupProfile([string[]]$Names) {
    $keys=@(Get-ChildItem 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VolumeCaches' -ErrorAction Stop)
    $selected=@($keys | Where-Object {$Names -contains $_.PSChildName})
    if(!$selected.Count){throw 'Категории очистки не найдены.'}
    do {
        $number=Get-Random -Minimum 1000 -Maximum 9999
        $flag='StateFlags{0:D4}' -f $number
    } while(@($keys | Where-Object {$null -ne (Get-ItemProperty -LiteralPath $_.PSPath -Name $flag -ErrorAction SilentlyContinue)}).Count)
    $profile=@{Number=$number;Flag=$flag;Keys=@()}
    try {
        foreach($key in $selected){New-ItemProperty -LiteralPath $key.PSPath -Name $flag -Value 2 -PropertyType DWord -ErrorAction Stop | Out-Null; $profile.Keys += $key.PSPath}
        return $profile
    } catch {Remove-CleanupProfile $profile; throw}
}
function Remove-CleanupProfile($Profile) {
    foreach($key in $Profile.Keys){Remove-ItemProperty -LiteralPath $key -Name $Profile.Flag -ErrorAction Continue}
}
function Invoke-OriginalUserCleanup([string]$Folder,[string]$SignalRoot=$Folder) {
    if(!$Folder){throw 'Нет исходного пользовательского процесса. Запускайте Start.cmd обычным двойным щелчком.'}
    $folderFull=[IO.Path]::GetFullPath($Folder)
    if($folderFull -notmatch '\\ServiceMaintenance\\UserJobs\\[a-f0-9]{32}$'){throw 'Неожиданный каталог пользовательского задания.'}
    # Never write through a redirected path, and never execute this user's files as admin.
    $node=Get-Item -LiteralPath $folderFull -ErrorAction Stop
    while($node){if($node.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Перенаправленный каталог задания не поддерживается.'}; $node=$node.Parent}
    if(!(Test-Path -LiteralPath (Join-Path $Folder 'ready.txt'))){throw 'Пользовательский процесс не подтвердил запуск.'}
    if(Get-Process cleanmgr,dism,sfc -ErrorAction SilentlyContinue){throw 'Уже идёт очистка или восстановление Windows.'}
    $profile=New-CleanupProfile $script:UserCleanupNames
    $complete=$false
    $signalled=$false
    try {
        $signal=Join-Path $SignalRoot 'go.txt'
        $stream=New-Object IO.FileStream($signal,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
        try {$bytes=[Text.Encoding]::ASCII.GetBytes([string]$profile.Number); $stream.Write($bytes,0,$bytes.Length)} finally {$stream.Dispose()}
        $signalled=$true
        $deadline=(Get-Date).AddHours(2)
        while(!(Test-Path -LiteralPath (Join-Path $Folder 'done.txt'))) {
            if((Get-Date) -gt $deadline){throw 'Пользовательская очистка не завершилась за 2 часа; следующие этапы не запущены.'}
            Start-Sleep -Seconds 3
        }
        $complete=$true
        'Пользовательский этап завершён.'
    } finally {
        if($complete -or !$signalled){Remove-CleanupProfile $profile}
        else {Write-Host ('Профиль очистки оставлен до завершения процесса: '+$profile.Flag)}
    }
}
function Invoke-Cleanup([switch]$Preview) {
    Section 'Очистка штатными средствами Windows'
    $base = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VolumeCaches'
    $keys = @(Get-ChildItem -LiteralPath $base)
    $selected = @($keys | Where-Object { $script:CleanupNames -contains $_.PSChildName })
    if(!$script:CompactOutput -or $Preview) {
        $selected | ForEach-Object { Write-Host "  $($_.PSChildName)" }
        Write-Host 'Только согласованные категории. Загрузки и профили браузеров не очищаются.'
    }
    $currentIdentity = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    if($currentIdentity -ne $OriginalIdentity) {
        Write-Host "ВНИМАНИЕ: запущено под $currentIdentity. Очистка пользовательских категорий может не охватить профиль $OriginalIdentity." -ForegroundColor Yellow
    }
    if($Preview) { return }
    if(Get-Command Assert-DiskTestIdle -ErrorAction SilentlyContinue){Assert-DiskTestIdle}
    if(Get-Process dism,sfc -ErrorAction SilentlyContinue) { throw 'Фоновое восстановление уже идёт. Повторная очистка доступна после его завершения.' }
    if(!$script:Admin) { throw 'Для очистки запустите Start.cmd от имени администратора.' }
    if(!$selected.Count) { throw 'Категории очистки не найдены.' }
    $cleanmgr = Join-Path $env:windir 'System32\cleanmgr.exe'
    if(!(Test-Path -LiteralPath $cleanmgr)) { throw 'Штатная очистка cleanmgr недоступна.' }
    if(Get-Process cleanmgr -ErrorAction SilentlyContinue) { throw 'Дождитесь завершения уже открытой очистки.' }
    $profile = Get-Random -Minimum 1000 -Maximum 9999
    $flag = 'StateFlags{0:D4}' -f $profile
    while(@($keys | Where-Object { $null -ne (Get-ItemProperty -LiteralPath $_.PSPath -Name $flag -ErrorAction SilentlyContinue) }).Count) {
        $profile = Get-Random -Minimum 1000 -Maximum 9999; $flag = 'StateFlags{0:D4}' -f $profile
    }
    $before = @(Get-WmiObject Win32_LogicalDisk -Filter 'DriveType=3')
    $changed = @()
    try {
        foreach($key in $selected) { New-ItemProperty -LiteralPath $key.PSPath -Name $flag -PropertyType DWord -Value 2 | Out-Null; $changed += $key.PSPath }
        $p = Start-Process -FilePath $cleanmgr -ArgumentList "/sagerun:$profile" -WindowStyle Normal -PassThru -Wait
        Write-Host "Штатная очистка завершила работу, код: $($p.ExitCode)."
    } finally { foreach($key in $changed) { Remove-ItemProperty -LiteralPath $key -Name $flag -ErrorAction Continue } }
    $changes=@(Get-WmiObject Win32_LogicalDisk -Filter 'DriveType=3' | ForEach-Object {
        $currentDisk = $_
        $old = $before | Where-Object { $_.DeviceID -eq $currentDisk.DeviceID }
        New-Object PSObject -Property @{Disk=$_.DeviceID; FreeGB=[math]::Round($_.FreeSpace/1GB,2); ChangeGB=[math]::Round(($_.FreeSpace-$old.FreeSpace)/1GB,2)}
    })
    $script:CleanupResult=@{ExitCode=$p.ExitCode;ChangeGB=($changes | Measure-Object ChangeGB -Sum).Sum}
    if(!$script:CompactOutput) {Table $changes; Write-Host 'Изменение места включает фоновую работу системы.'}
}
