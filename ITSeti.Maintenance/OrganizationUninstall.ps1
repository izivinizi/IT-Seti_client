<#
    Uninstall.ps1  -  удаление рабочего места "ИТ сети" с выбором компонентов

    Сначала ищет, что стоит на ПК, и показывает окно с галочками (найденное отмечено,
    ненайденное неактивно, есть пункт "Удалить всё"). Удаляет только отмеченное, в таком порядке:
      1. Desktop Info + панель - один пункт: процесс, автозапуск для всех, ветка HKCU\SOFTWARE\ITSETI,
                                 папка C:\ProgramData\ITSETI, затем unins000.exe /VERYSILENT
      2. OCS Inventory agent   - останавливает службу и значок в трее, uninst.exe /S; если деинсталлятора
                                 нет или он не справился - sc delete, папка, автозапуск, запись в реестре
      3. RMS Host              - msiexec /x по UpgradeCode, любая версия
      4. AnyDesk               - AnyDesk.exe --remove --silent
    Что НЕ трогает (чтобы при повторной установке сохранились ID):
      C:\ProgramData\AnyDesk (конфиг и ID AnyDesk), настройки RMS Host в реестре (Internet-ID),
      C:\ProgramData\OCS Inventory NG (конфиг агента).
    В конце показывает окно с итогом.

    Запускать через Удалить.vbs (один запрос UAC, никаких окон кроме выбора и итога).
    Ключи:
      -All     без окна выбора, удалить всё найденное
      -Only    без окна выбора, только указанное: -Only OCS,RMS  (DesktopInfo, OCS, RMS, AnyDesk)
      -DryRun  только проверить, что было бы сделано, ничего не удаляя
    Лог: C:\ProgramData\ITSETI-uninstall.log
#>
param([switch]$DryRun, [switch]$All, [ValidateSet('DesktopInfo', 'OCS', 'RMS', 'AnyDesk')][string[]]$Only)

$ErrorActionPreference = 'Continue'
$DataDir = Join-Path $env:ProgramData 'ITSETI'
$LogFile = Join-Path $env:ProgramData 'ITSETI-uninstall.log'
$PF86    = if (${env:ProgramFiles(x86)}) { ${env:ProgramFiles(x86)} } else { $env:ProgramFiles }
$RmsUpgradeCode = '{FE83B905-4554-4DFF-97F4-9292178CB171}'
$PanelLnk = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\Startup\ITSETI Desktop Info.lnk'
$DiDir   = Join-Path $env:ProgramFiles 'Desktop Info'
$OcsDir  = Join-Path $env:ProgramFiles 'OCS Inventory Agent'
$OcsService = 'OCS Inventory Service'
$OcsUninstallKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\OCS Inventory NG Agent'
$Results = New-Object System.Collections.Generic.List[string]

function Log([string]$msg) {
    $line = '{0:yyyy-MM-dd HH:mm:ss}  {1}' -f (Get-Date), $msg
    Add-Content -Path $LogFile -Value $line -Encoding UTF8
}
function Result([string]$name, [string]$status) {
    $Results.Add(('{0,-14} {1}' -f ($name + ':'), $status)); Log "[$name] $status"
}
function Run-Hidden([string]$file, [string]$arguments, [int]$timeoutSec = 600) {
    if ($DryRun) { Log "DRY-RUN: $file $arguments"; return 0 }
    Log "RUN  $file $arguments"
    try {
        $p = Start-Process -FilePath $file -ArgumentList $arguments -WindowStyle Hidden -PassThru -ErrorAction Stop
    } catch { Log "FAIL start: $($_.Exception.Message)"; return -2 }
    if (-not $p.WaitForExit($timeoutSec * 1000)) { try { $p.Kill() } catch {}; Log "TIMEOUT $file"; return -1 }
    Log "EXIT $($p.ExitCode)  $file"
    return $p.ExitCode
}
function Wait-Until([scriptblock]$cond, [int]$timeoutSec) {
    if ($DryRun) { return $true }
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $deadline) { if (& $cond) { return $true }; Start-Sleep -Seconds 2 }
    return [bool](& $cond)
}
function Show-Box([string]$text, [string]$title, [string]$icon = 'Information') {
    Add-Type -AssemblyName System.Windows.Forms
    $owner = New-Object System.Windows.Forms.Form -Property @{ TopMost = $true; ShowInTaskbar = $false }
    [System.Media.SystemSounds]::Asterisk.Play()
    [System.Windows.Forms.MessageBox]::Show($owner, $text, $title, 'OK', $icon) | Out-Null
}
function Delete-Path([string]$path) {
    if (-not (Test-Path $path)) { return }
    if ($DryRun) { Log "DRY-RUN: удалить $path"; return }
    try {
        if (Test-Path $path -PathType Container) { [System.IO.Directory]::Delete($path, $true) } else { [System.IO.File]::Delete($path) }
        Log "удалено $path"
    } catch { Log "не удалось удалить $path : $($_.Exception.Message)" }
}
function Get-RmsProducts {
    $wi = New-Object -ComObject WindowsInstaller.Installer
    $list = @()
    try { $rel = $wi.GetType().InvokeMember('RelatedProducts', 'GetProperty', $null, $wi, @($RmsUpgradeCode)) } catch { return $list }
    foreach ($pc in $rel) {
        try { $ver = $wi.GetType().InvokeMember('ProductInfo', 'GetProperty', $null, $wi, @($pc, 'VersionString')) } catch { continue }
        $list += [pscustomobject]@{ Code = $pc; Version = $ver }
    }
    return $list
}
# через sc.exe, а не Get-Service: объект службы держит на неё дескриптор, и помеченная
# на удаление служба не исчезает, пока скрипт работает
function Test-ServiceExists([string]$name) {
    & sc.exe query "$name" | Out-Null
    return $LASTEXITCODE -ne 1060
}
function Get-AnyDeskExe {
    foreach ($c in @("$env:ProgramFiles\AnyDesk\AnyDesk.exe", "$PF86\AnyDesk\AnyDesk.exe")) { if (Test-Path $c) { return $c } }
    return $null
}
function Get-FileVer([string]$path) {
    # у некоторых exe (Desktop Info) в версии мусорные символы - берём только цифры с точками
    if ((Test-Path $path) -and (Get-Item $path).VersionInfo.FileVersion -match '^\d+(\.\d+)*') { return ", v$($Matches[0])" }
    return ''
}

# окно выбора: $items - список @{Key; Title; Found; State}, возвращает ключи отмеченных или $null при отмене
function Select-Components($items) {
    Add-Type -AssemblyName System.Windows.Forms, System.Drawing
    [System.Windows.Forms.Application]::EnableVisualStyles()
    $pad = { param($l, $t, $r, $b) New-Object System.Windows.Forms.Padding $l, $t, $r, $b }
    $form = New-Object System.Windows.Forms.Form -Property @{
        Text = 'ИТ сети - удаление' + $(if ($DryRun) { ' (проверка)' }); StartPosition = 'CenterScreen'; TopMost = $true
        FormBorderStyle = 'FixedDialog'; MaximizeBox = $false; MinimizeBox = $false
        AutoSize = $true; AutoSizeMode = 'GrowAndShrink'; Padding = (& $pad 14 12 14 12)
        Font = New-Object System.Drawing.Font('Segoe UI', 9)
    }
    $layout = New-Object System.Windows.Forms.FlowLayoutPanel -Property @{ FlowDirection = 'TopDown'; WrapContents = $false; AutoSize = $true }
    $form.Controls.Add($layout)
    $layout.Controls.Add((New-Object System.Windows.Forms.Label -Property @{
        Text = "Компьютер: $env:COMPUTERNAME`nОтметьте, что удалить:"; AutoSize = $true; Margin = (& $pad 0 0 0 10) }))

    $boxes = [ordered]@{}
    $allBox = New-Object System.Windows.Forms.CheckBox -Property @{
        Text = 'Удалить всё'; AutoSize = $true; Margin = (& $pad 0 0 0 6)
        Font = New-Object System.Drawing.Font('Segoe UI', 9, [System.Drawing.FontStyle]::Bold) }
    $layout.Controls.Add($allBox)
    foreach ($it in $items) {
        $cb = New-Object System.Windows.Forms.CheckBox -Property @{
            Text = "$($it.Title)  -  $($it.State)"; AutoSize = $true; Checked = $it.Found; Enabled = $it.Found; Margin = (& $pad 18 2 0 2) }
        $layout.Controls.Add($cb); $boxes[$it.Key] = $cb
    }
    $note = New-Object System.Windows.Forms.Label -Property @{ AutoSize = $true; Margin = (& $pad 0 10 0 0); ForeColor = [System.Drawing.Color]::DimGray
        Text = "ID AnyDesk и RMS сохраняются - при повторной установке будут прежними." }
    $layout.Controls.Add($note)

    $buttons = New-Object System.Windows.Forms.FlowLayoutPanel -Property @{ FlowDirection = 'RightToLeft'; AutoSize = $true; Anchor = 'Right'; Margin = (& $pad 0 12 0 0) }
    $cancel = New-Object System.Windows.Forms.Button -Property @{ Text = 'Отмена'; DialogResult = 'Cancel'; AutoSize = $true }
    $ok = New-Object System.Windows.Forms.Button -Property @{ Text = 'Удалить'; DialogResult = 'OK'; AutoSize = $true }
    $buttons.Controls.Add($cancel); $buttons.Controls.Add($ok)
    $layout.Controls.Add($buttons)
    $form.CancelButton = $cancel

    # общая синхронизация: "Удалить всё" и доступность кнопки
    $state = @{ busy = $false }
    $sync = {
        if ($state.busy) { return }
        $state.busy = $true
        $enabled = @($boxes.Values | Where-Object { $_.Enabled })
        $allBox.Enabled = $enabled.Count -gt 0
        $allBox.Checked = $enabled.Count -gt 0 -and -not ($enabled | Where-Object { -not $_.Checked })
        $ok.Enabled = [bool]($boxes.Values | Where-Object { $_.Checked })
        $state.busy = $false
    }.GetNewClosure()
    $toggleAll = {
        if ($state.busy) { return }
        $state.busy = $true
        foreach ($cb in $boxes.Values) { if ($cb.Enabled) { $cb.Checked = $allBox.Checked } }
        $state.busy = $false
        & $sync
    }.GetNewClosure()
    $allBox.Add_Click($toggleAll)
    foreach ($cb in $boxes.Values) { $cb.Add_CheckedChanged($sync) }
    & $sync

    [System.Media.SystemSounds]::Question.Play()
    if ($form.ShowDialog() -ne 'OK') { return $null }
    return @($boxes.Keys | Where-Object { $boxes[$_].Checked })
}

# ---------------------------------------------------------------- старт
Log ('================ старт удаления' + $(if ($DryRun) { ' (DRY-RUN)' }) + ' ================')
Log "Компьютер: $env:COMPUTERNAME, запуск от: $env:USERNAME, ключи: -All:$All -DryRun:$DryRun"
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin -and -not $DryRun) {
    Log 'Нет прав администратора, выход.'
    Show-Box "Деинсталлятор запущен без прав администратора.`n`nЗапустите файл Удалить.vbs и подтвердите запрос UAC." 'ИТ сети - удаление' 'Warning'
    exit 1
}

# ---------------------------------------------------------------- что стоит на ПК
$rmsProducts = @(Get-RmsProducts)
$rmsSvc = Get-Service -Name RManService -ErrorAction SilentlyContinue
$ocsSvc = Test-ServiceExists $OcsService
$adExe  = Get-AnyDeskExe
$panelFound = [bool]((Test-Path $PanelLnk) -or (Test-Path $DataDir) -or (Test-Path 'HKCU:\SOFTWARE\ITSETI') -or
    (Get-ChildItem 'C:\Users\*\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup\ITSETI Desktop Info.lnk' -ErrorAction SilentlyContinue))
$items = @(
    @{ Key = 'DesktopInfo'; Title = 'Desktop Info + панель'; Found = [bool]((Test-Path $DiDir) -or $panelFound); Ver = Get-FileVer (Join-Path $DiDir 'DesktopInfo.exe') }
    @{ Key = 'OCS';         Title = 'OCS Agent';      Found = [bool]((Test-Path $OcsDir) -or $ocsSvc -or (Test-Path $OcsUninstallKey)); Ver = Get-FileVer (Join-Path $OcsDir 'OCSInventory.exe') }
    @{ Key = 'RMS';         Title = 'RMS Host';       Found = [bool]($rmsProducts -or $rmsSvc); Ver = $(if ($rmsProducts) { ", v$($rmsProducts[0].Version)" } else { '' }) }
    @{ Key = 'AnyDesk';     Title = 'AnyDesk';        Found = [bool]$adExe; Ver = $(if ($adExe) { Get-FileVer $adExe } else { '' }) }
)
foreach ($it in $items) { $it.State = if ($it.Found) { 'установлен' + $it.Ver } else { 'не найден' } }
Log ('Найдено: ' + (($items | ForEach-Object { "$($_.Title) - $($_.State)" }) -join '; '))

if (-not ($items | Where-Object { $_.Found })) {
    Log 'Удалять нечего.'
    Show-Box "Компьютер: $env:COMPUTERNAME`n`nНи один компонент ИТ сети не найден, удалять нечего." 'ИТ сети - удаление'
    exit 0
}
if ($All -or $Only) {
    $Selected = @($items | Where-Object { $_.Found -and (-not $Only -or $Only -contains $_.Key) } | ForEach-Object { $_.Key })
} else {
    $Selected = Select-Components $items
    if ($null -eq $Selected) { Log 'Отменено пользователем.'; Log '================ конец ================'; exit 3 }
}
Log ('Выбрано: ' + ($Selected -join ', '))

# ---------------------------------------------------------------- 1. Desktop Info + панель
if ($Selected -contains 'DesktopInfo') {
    try {
        # сначала панель: процесс, автозапуск, настройки, папка ProgramData\ITSETI
        $procs = Get-Process -Name 'DesktopInfo*' -ErrorAction SilentlyContinue
        if ($procs -and -not $DryRun) { $procs | Stop-Process -Force -ErrorAction SilentlyContinue }
        Delete-Path $PanelLnk
        Get-ChildItem 'C:\Users\*\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup\ITSETI Desktop Info.lnk' -ErrorAction SilentlyContinue |
            ForEach-Object { Delete-Path $_.FullName }
        if ((Test-Path 'HKCU:\SOFTWARE\ITSETI') -and -not $DryRun) { [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree('SOFTWARE\ITSETI', $false) }
        Delete-Path $DataDir
        Log 'Панель: процесс, автозапуск, настройки и папка ProgramData\ITSETI убраны'
        # потом сама программа
        $diExe = Join-Path $DiDir 'DesktopInfo.exe'
        $unins = Join-Path $DiDir 'unins000.exe'
        if (Test-Path $unins) {
            $code = Run-Hidden $unins '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART' 300
            $gone = Wait-Until { -not (Test-Path $diExe) } 90
            if ($gone) { Delete-Path $DiDir; Result 'Desktop Info' 'удалён вместе с панелью' }
            else { Result 'Desktop Info' "ОШИБКА: файлы остались (код $code), панель убрана" }
        } elseif (Test-Path $DiDir) {
            Delete-Path $DiDir
            Result 'Desktop Info' 'удалён вместе с панелью (деинсталлятора не было, папка стёрта)'
        } else { Result 'Desktop Info' 'программы не было, остатки панели убраны' }
    } catch { Result 'Desktop Info' "ОШИБКА: $($_.Exception.Message)" }
}

# ---------------------------------------------------------------- 2. OCS Inventory agent
if ($Selected -contains 'OCS') {
    try {
        $ocsUn = Join-Path $OcsDir 'uninst.exe'
        # служба и значок в трее держат файлы - без их остановки папка не удаляется
        if (-not $DryRun) {
            Stop-Service -Name $OcsService -Force -ErrorAction SilentlyContinue
            Get-Process -Name 'Ocs*', 'OCSInventory' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
        }
        if (Test-Path $ocsUn) {
            # _?= заставляет NSIS-деинсталлятор работать на месте и синхронно
            $code = Run-Hidden $ocsUn ('/S _?={0}' -f $OcsDir) 300
            $how = "деинсталлятором, код $code"
        } else {
            Log "OCS: нет $ocsUn, удаляю вручную"
            $how = 'вручную, деинсталлятора не было'
        }
        $gone = Wait-Until { -not (Test-ServiceExists $OcsService) } 30
        if (-not $gone) {
            # деинсталлятора не было или он не снял службу - снимаем сами
            Run-Hidden 'sc.exe' ('delete "{0}"' -f $OcsService) 30 | Out-Null
            $gone = Wait-Until { -not (Test-ServiceExists $OcsService) } 30
        }
        if (-not $DryRun) { Get-Process -Name 'Ocs*', 'OCSInventory' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue }
        Delete-Path $OcsDir
        Delete-Path (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\Startup\OCS Inventory NG Systray.lnk')
        if ((Test-Path $OcsUninstallKey) -and -not $DryRun) { Remove-Item $OcsUninstallKey -Recurse -Force -ErrorAction SilentlyContinue; Log 'OCS: запись в "Программах и компонентах" удалена' }
        if (-not $gone) { Result 'OCS Agent' "ОШИБКА: служба осталась ($how)" }
        elseif ((Test-Path $OcsDir) -and -not $DryRun) { Result 'OCS Agent' "удалён ($how), часть файлов занята - удалите папку $OcsDir после перезагрузки" }
        else { Result 'OCS Agent' "удалён ($how)" }
    } catch { Result 'OCS Agent' "ОШИБКА: $($_.Exception.Message)" }
}

# ---------------------------------------------------------------- 3. RMS Host
if ($Selected -contains 'RMS') {
    try {
        if ($rmsProducts -or $rmsSvc) {
            $codes = @()
            foreach ($p in $rmsProducts) {
                Log "RMS Host: найден продукт $($p.Code) v$($p.Version)"
                $codes += Run-Hidden 'msiexec.exe' ('/x {0} /qn /norestart REBOOT=ReallySuppress /l*v "C:\ProgramData\ITSETI-rms-uninstall.log"' -f $p.Code) 600
            }
            $gone = Wait-Until { -not (Get-Service -Name RManService -ErrorAction SilentlyContinue) } 90
            $bad = $codes | Where-Object { $_ -notin 0, 3010, 1641, 1605 }
            if ($gone -and -not $bad) {
                $note = if ($codes -contains 3010) { ', желательна перезагрузка' } else { '' }
                Result 'RMS Host' ('удалён' + $note)
            } elseif (-not $rmsProducts -and $rmsSvc) {
                Result 'RMS Host' 'ОШИБКА: служба есть, но MSI-запись не найдена, удалите вручную'
            } else { Result 'RMS Host' "ОШИБКА: код msiexec $($bad -join ',') (см. ITSETI-rms-uninstall.log)" }
        } else { Result 'RMS Host' 'не установлен' }
    } catch { Result 'RMS Host' "ОШИБКА: $($_.Exception.Message)" }
}

# ---------------------------------------------------------------- 4. AnyDesk
if ($Selected -contains 'AnyDesk') {
    try {
        if ($adExe) {
            $code = Run-Hidden $adExe '--remove --silent' 300
            $gone = Wait-Until { -not (Test-Path $adExe) } 90
            Result 'AnyDesk' $(if ($gone) { 'удалён (конфиг с ID в ProgramData\AnyDesk оставлен)' } else { "ОШИБКА: файлы остались (код $code)" })
        } else { Result 'AnyDesk' 'не установлен' }
    } catch { Result 'AnyDesk' "ОШИБКА: $($_.Exception.Message)" }
}

# ---------------------------------------------------------------- итог
$summary = @()
$summary += "Компьютер:  $env:COMPUTERNAME"
if ($DryRun) { $summary += 'РЕЖИМ ПРОВЕРКИ: ничего не удалялось'; $summary += '' }
$summary += $Results
$kept = @($items | Where-Object { $_.Found -and $Selected -notcontains $_.Key } | ForEach-Object { $_.Title })
if ($kept) { $summary += ''; $summary += 'Оставлено: ' + ($kept -join ', ') }
$summary += ''
$summary += "Лог: $LogFile"
$text = $summary -join "`n"
Log "ИТОГ:`n$text"
Log '================ конец ================'
$hasErr = ($Results -join ' ') -match 'ОШИБКА'
$title = if ($hasErr) { 'ИТ сети - удаление завершено с ошибками' } else { 'ИТ сети - удаление завершено' }
Show-Box $text $title $(if ($hasErr) { 'Warning' } else { 'Information' })
if ($hasErr) { exit 2 }
