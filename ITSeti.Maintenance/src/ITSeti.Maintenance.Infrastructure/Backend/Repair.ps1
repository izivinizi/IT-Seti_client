function Get-RepairPlan([version]$Version,[string]$SystemDirectory) {
    if($Version -ge [version]'6.2') {
        New-Object PSObject -Property @{Name='DISM';Path=(Join-Path $SystemDirectory 'dism.exe');Arguments=@('/Online','/Cleanup-Image','/RestoreHealth','/NoRestart')}
    }
    New-Object PSObject -Property @{Name='SFC';Path=(Join-Path $SystemDirectory 'sfc.exe');Arguments=@('/scannow')}
}
function Invoke-SystemRepair([switch]$Preview) {
    Section 'Проверка и восстановление системных файлов'
    $version = [version](Get-WmiObject Win32_OperatingSystem -ErrorAction Stop).Version
    $systemDirectory = Join-Path $env:windir 'System32'
    if([IntPtr]::Size -eq 4 -and $env:PROCESSOR_ARCHITEW6432) { $systemDirectory=Join-Path $env:windir 'Sysnative' }
    $plan = @(Get-RepairPlan $version $systemDirectory)
    foreach($step in $plan) { Write-Host ($step.Name+': '+($step.Arguments -join ' ')) }
    if($version -lt [version]'6.2') { Write-Host 'В Windows 7 DISM /RestoreHealth не поддерживается; выполняется SFC.' }
    Write-Host 'Прогресс выводится ниже. Перезагрузка автоматически не выполняется. DISM может обращаться к Windows Update.'
    if($Preview) { return }
    if(!$script:Admin) { throw 'Восстановление требует прав администратора. Перезапустите комплект с повышением прав.' }
    if(Get-Process dism,sfc,cleanmgr -ErrorAction SilentlyContinue) { throw 'Дождитесь уже запущенной проверки/очистки Windows.' }
    $script:RepairCodes=@()
    foreach($step in $plan) {
        if(!(Test-Path -LiteralPath $step.Path -PathType Leaf)) { throw "Не найдена системная утилита: $($step.Path)" }
        Section $step.Name
        $arguments = $step.Arguments
        & $step.Path @arguments | Out-Host
        $code = $LASTEXITCODE
        $script:RepairCodes+=($step.Name+': код '+$code)
        $script:RepairCodes | Set-Content -LiteralPath (Join-Path $JobRoot 'steps.txt') -Encoding UTF8
        Write-Host "Код завершения $($step.Name): $code"
        if($code -eq 3010) { $script:RepairRestartRequired=$true; Write-Host 'Windows запрашивает перезагрузку. Следующий этап отложен до неё.' -ForegroundColor Yellow; return }
        if($code -ne 0) { $script:RepairFailed=$true; Write-Host 'Этап завершился с ненулевым кодом. Смотрите сообщение утилиты; успешное восстановление не подтверждено.' -ForegroundColor Yellow }
    }
    Write-Host "Итог SFC смотрите в его сообщении выше. Подробности: $env:windir\Logs\CBS\CBS.log"
    if($version -ge [version]'6.2') { Write-Host "Журнал DISM: $env:windir\Logs\DISM\dism.log" }
}
