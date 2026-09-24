param([string]$JobRoot,[string]$SignalRoot=$JobRoot,[switch]$TestOnly)
$ErrorActionPreference='Stop'
function Status([string]$Text) { $Text | Set-Content -LiteralPath (Join-Path $JobRoot 'user-status.txt') -Encoding UTF8 }
try {
    [Diagnostics.Process]::GetCurrentProcess().PriorityClass='BelowNormal'
    $identity=[Security.Principal.WindowsIdentity]::GetCurrent().Name
    Status ("Ожидает тестов | пользователь: "+$identity)
    'ready' | Set-Content -LiteralPath (Join-Path $JobRoot 'ready.txt')
    $signal=Join-Path $SignalRoot 'go.txt'
    $deadline=(Get-Date).AddHours(1)
    $localSignal=Join-Path $JobRoot 'go.txt'
    while(!(Test-Path -LiteralPath $signal) -and !(Test-Path -LiteralPath $localSignal)) {
        if((Get-Date) -gt $deadline){throw 'Запуск не подтверждён за час. Очистка не выполнялась.'}
        Start-Sleep -Seconds 2
    }
    $chosenSignal=if(Test-Path -LiteralPath $signal){$signal}else{$localSignal}
    $value=[IO.File]::ReadAllText($chosenSignal).Trim()
    if($value -eq 'abort'){Status ('Очистка не запускалась | пользователь: '+$identity); return}
    if($value -notmatch '^\d{4}$' -and $value -ne 'interactive'){throw 'Некорректный режим очистки.'}
    Status ("Очистка профиля "+$identity)
    if(!$TestOnly) {
        # Use the existing token, never ShellExecute/RunAs. Fail rather than change identity.
        $start=New-Object Diagnostics.ProcessStartInfo
        $start.FileName=Join-Path $env:windir 'System32\cleanmgr.exe'
        if($value -eq 'interactive'){$start.Arguments='/d '+$env:SystemDrive.TrimEnd(':'); Status ('Открыта очистка '+$identity+'. Выберите доступные категории и подтвердите очистку в окне Windows.')}
        else {$start.Arguments='/sagerun:'+([int]$value)}
        $start.UseShellExecute=$false
        $start.CreateNoWindow=$false
        $start.WindowStyle='Normal'
        $p=[Diagnostics.Process]::Start($start)
        $p.WaitForExit()
        if($p.ExitCode -ne 0){throw ('cleanmgr: код '+$p.ExitCode)}
    }
    if($value -eq 'interactive' -and !$TestOnly){Status ('Окно очистки закрыто | '+$identity+'. Выполнение зависит от выбранных и подтверждённых действий.')}
    else {Status ("Завершена | пользователь: "+$identity+$(if($TestOnly){' | TEST OK'}))}
} catch { Status ('Не выполнена: '+$_.Exception.Message) }
finally { 'done' | Set-Content -LiteralPath (Join-Path $JobRoot 'done.txt') }
