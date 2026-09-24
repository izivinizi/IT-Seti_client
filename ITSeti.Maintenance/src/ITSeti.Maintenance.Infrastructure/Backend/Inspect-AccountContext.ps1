# Reports only the identity and token of this process. It does not enumerate accounts or use credentials.
$ErrorActionPreference = 'Stop'

try {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    $isElevated = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    $sessionId = [Diagnostics.Process]::GetCurrentProcess().SessionId
    $computer = Get-WmiObject -Class Win32_ComputerSystem -ErrorAction Stop

    Write-Output ('Компьютер: ' + $computer.Name)
    if ($computer.PartOfDomain) {
        Write-Output ('Домен: ' + $computer.Domain)
    } else {
        Write-Output ('Рабочая группа: ' + $computer.Workgroup)
    }
    Write-Output ('Текущая учётная запись: ' + $identity.Name)
    Write-Output ('Сеанс процесса: ' + $sessionId)
    if ($isElevated) {
        Write-Output 'Администраторский токен этого процесса: да'
    } else {
        Write-Output 'Администраторский токен этого процесса: нет (учётная запись может иметь право повышения через UAC)'
    }
} catch {
    Write-Error ('Не удалось получить контекст текущей учётной записи: ' + $_.Exception.Message)
    exit 1
}

Write-Output ''
Write-Output 'Служебные задачи приложения:'
foreach ($taskName in @('ITSeti-Maintenance-Full', 'ITSeti-Maintenance-QuickFull', 'ITSeti-Maintenance-Cleanup', 'ITSeti-Maintenance-Repair')) {
    & "$env:WINDIR\System32\schtasks.exe" /Query /TN $taskName 2>$null | Out-Null
    if ($LASTEXITCODE -eq 0) {
        Write-Output ('  ' + $taskName + ': зарегистрирована')
    } else {
        Write-Output ('  ' + $taskName + ': не найдена')
    }
}

Write-Output ''
Write-Output 'Список других локальных или доменных учётных записей этим скриптом не запрашивается.'
