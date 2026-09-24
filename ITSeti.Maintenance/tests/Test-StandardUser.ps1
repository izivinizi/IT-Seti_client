param([Parameter(Mandatory=$true)][string]$RunRoot,[switch]$Worker,[string]$LegacyRoot)
$ErrorActionPreference='Stop'
if($Worker) {
    try {
        $identity=[Security.Principal.WindowsIdentity]::GetCurrent()
        $admin=([Security.Principal.WindowsPrincipal]$identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
        ('Identity='+$identity.Name+'; Admin='+$admin) | Set-Content -LiteralPath (Join-Path $RunRoot 'identity.txt')
        Set-Location -LiteralPath $RunRoot
        & (Join-Path $RunRoot 'Smoke\ITSeti.Maintenance.Smoke.exe') --installed-user-check *> (Join-Path $RunRoot 'user-test.log')
        $LASTEXITCODE | Set-Content -LiteralPath (Join-Path $RunRoot 'user-test.exit')
    } catch { $_ | Out-String | Set-Content -LiteralPath (Join-Path $RunRoot 'user-test.error'); exit 1 }
    exit
}
if($LegacyRoot) {
    $legacyOutput=Join-Path $RunRoot 'Legacy'
    New-Item -ItemType Directory -Path $legacyOutput -Force | Out-Null
    & ([scriptblock]::Create([IO.File]::ReadAllText((Join-Path $RunRoot 'Test-NewDiskMark.ps1')))) -Root $LegacyRoot -OutputFolder $legacyOutput -SkipScan
}
$task='ITSeti-Test-'+[guid]::NewGuid().ToString('N')
try {
    $user=Get-LocalUser test -ErrorAction Stop
    $scriptPath=Join-Path $RunRoot 'Test-StandardUser.ps1'
    if([IO.Path]::GetFullPath($PSCommandPath) -ne [IO.Path]::GetFullPath($scriptPath)) {
        Copy-Item -LiteralPath $PSCommandPath -Destination $scriptPath
    }
    & icacls.exe $RunRoot /grant ('*'+$user.SID.Value+':(OI)(CI)M') | Out-Null
    if($LASTEXITCODE){throw 'Unable to grant test output access'}
    $action=New-ScheduledTaskAction -Execute "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" -Argument ('-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "'+$scriptPath+'" -Worker -RunRoot "'+$RunRoot+'"') -WorkingDirectory $RunRoot
    $principal=New-ScheduledTaskPrincipal -UserId ($env:COMPUTERNAME+'\test') -LogonType S4U -RunLevel Limited
    $settings=New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes 15) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
    Register-ScheduledTask -TaskName $task -Action $action -Principal $principal -Settings $settings -Force | Out-Null
    Start-ScheduledTask -TaskName $task
    $deadline=(Get-Date).AddMinutes(14)
    do {
        Start-Sleep -Seconds 2
        $info=Get-ScheduledTaskInfo -TaskName $task
        $state=(Get-ScheduledTask -TaskName $task).State
    } while($state -eq 'Running' -and (Get-Date) -lt $deadline)
    $info | Format-List * | Out-String | Set-Content -LiteralPath (Join-Path $RunRoot 'task-result.txt')
} catch { $_ | Out-String | Set-Content -LiteralPath (Join-Path $RunRoot 'task-error.txt') }
finally {
    if(Get-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue) {
        Stop-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue
        Unregister-ScheduledTask -TaskName $task -Confirm:$false
    }
}
