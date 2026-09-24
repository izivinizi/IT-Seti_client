param([string]$JobRoot,[switch]$TestOnly,[switch]$WithCleanup,[string]$OriginalUserJob)
$ErrorActionPreference = 'Stop'
$script:Admin = (New-Object Security.Principal.WindowsPrincipal ([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$script:RepairFailed = $false
$script:RepairRestartRequired = $false
$mutex = New-Object Threading.Mutex($false,'Global\ServiceMaintenance-SystemRepair')
$ownsMutex = $false
$transcribing = $false
function Write-Status([string]$Text) { ((Get-Date -Format 'yyyy-MM-dd HH:mm:ss')+' | '+$Text) | Set-Content -LiteralPath (Join-Path $JobRoot 'status.txt') -Encoding UTF8 }
function Section([string]$Text) { Write-Host "`n=== $Text ==="; Write-Status $Text }
try {
    try { $ownsMutex=$mutex.WaitOne(0,$false) } catch [Threading.AbandonedMutexException] { $ownsMutex=$true }
    if(!$ownsMutex) { Write-Status 'Пропущено: другое фоновое восстановление уже выполняется.'; return }
    Start-Transcript -Path (Join-Path $JobRoot 'repair.log') -Force | Out-Null
    $transcribing=$true
    Write-Status 'Запущено'
    [Diagnostics.Process]::GetCurrentProcess().PriorityClass='BelowNormal'
    if($TestOnly) { Start-Sleep -Seconds 2; Write-Status 'TEST OK: локальный фоновый процесс работает; восстановление не запускалось.'; return }
    if(!$script:Admin){throw 'У процесса нет повышенных прав администратора.'}
    $script:CompactOutput=$true
    $OriginalIdentity=[Security.Principal.WindowsIdentity]::GetCurrent().Name
    if($WithCleanup) {
        . ([scriptblock]::Create([IO.File]::ReadAllText((Join-Path $JobRoot 'Cleanup.ps1'),[Text.Encoding]::UTF8)))
        Write-Status 'Очистка исходного пользователя'
        try {
            $userResult=Invoke-OriginalUserCleanup $OriginalUserJob
            $userResult | Set-Content -LiteralPath (Join-Path $JobRoot 'user-cleanup.txt') -Encoding UTF8
        } catch { ('Не выполнена: '+$_.Exception.Message) | Set-Content -LiteralPath (Join-Path $JobRoot 'user-cleanup.txt') -Encoding UTF8 }
        try {
            Write-Status 'Очистка администратора и файлов обновлений Windows'
            Invoke-Cleanup
            ('Код {0}; изменение свободного места {1:N2} ГБ (включая работу других программ).' -f $script:CleanupResult.ExitCode,$script:CleanupResult.ChangeGB) | Set-Content -LiteralPath (Join-Path $JobRoot 'admin-cleanup.txt') -Encoding UTF8
        } catch { ('Не выполнена: '+$_.Exception.Message) | Set-Content -LiteralPath (Join-Path $JobRoot 'admin-cleanup.txt') -Encoding UTF8 }
    }
    . ([scriptblock]::Create([IO.File]::ReadAllText((Join-Path $JobRoot 'Repair.ps1'),[Text.Encoding]::UTF8)))
    Invoke-SystemRepair
    $codes=if($script:RepairCodes){$script:RepairCodes -join '; '}else{'команды не выполнены'}
    if($script:RepairRestartRequired) { Write-Status ("Требуется перезагрузка. $codes. Автоматическая перезагрузка не выполнялась.") }
    elseif($script:RepairFailed) { Write-Status ("Завершено с ошибками. $codes. Подробности в repair.log.") }
    else { Write-Status ("Завершено. $codes. Вывод проверки в repair.log; нулевой код сам по себе не подтверждает целостность.") }
} catch { Write-Status ('Ошибка: '+$_.Exception.Message); Write-Output $_.Exception.ToString() }
finally {
    if($transcribing) { Stop-Transcript | Out-Null }
    if($ownsMutex) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
