$ErrorActionPreference='Stop'
$root=Join-Path $env:ProgramData 'ITSeti\Maintenance'
$marker=Join-Path $root 'installed.flag'
if(!(Test-Path -LiteralPath $marker -PathType Leaf)){exit 0}
if([Security.Principal.WindowsIdentity]::GetCurrent().User.Value -ne 'S-1-5-18'){throw 'Service recovery requires SYSTEM.'}
if(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -match '^ITSeti-Maintenance.*Setup' }){exit 0}
$exe=Join-Path (Split-Path $PSScriptRoot -Parent) 'Service\ITSeti.Maintenance.Service.exe'
$name='ITSetiMaintenanceService'
$result=Join-Path $root 'service-health.json'
try {
    if(!(Test-Path -LiteralPath $exe -PathType Leaf)){throw 'Файл службы отсутствует. Требуется восстановить установку приложения.'}
    $service=Get-Service -Name $name -ErrorAction SilentlyContinue
    $repaired=$false
    if(!$service){
        if(!(Test-Path -LiteralPath $marker)){exit 0}
        New-Service -Name $name -BinaryPathName ('"'+$exe+'"') -DisplayName 'ИТ-Сети: обслуживание ПК' -StartupType Automatic | Out-Null
        $repaired=$true
    }
    $service=Get-Service -Name $name -ErrorAction Stop
    $configuration=Get-CimInstance Win32_Service -Filter "Name='$name'" -ErrorAction Stop
    if($configuration.PathName.Trim('"') -ine $exe -or $configuration.StartName -notin @('LocalSystem','NT AUTHORITY\SYSTEM')){
        throw 'Регистрация службы не соответствует установленному приложению. Требуется восстановить установку.'
    }
    if($configuration.StartMode -ne 'Auto'){
        Set-Service -Name $name -StartupType Automatic -ErrorAction Stop
        $repaired=$true
    }
    if($service.Status -ne 'Running'){
        Start-Service -Name $name -ErrorAction Stop
        $service.WaitForStatus('Running',[TimeSpan]::FromSeconds(30))
        $repaired=$true
    }
    if($repaired){
        & sc.exe failure $name reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null
        if($LASTEXITCODE -ne 0){throw 'Не удалось настроить восстановление службы.'}
        & sc.exe failureflag $name 1 | Out-Null
        if($LASTEXITCODE -ne 0){throw 'Не удалось включить восстановление после аварийного выхода.'}
        [IO.File]::WriteAllText($result,(@{Success=$true;Recovered=$true;UpdatedAt=[DateTimeOffset]::UtcNow.ToString('O');Message='Служба восстановлена и запущена.'} | ConvertTo-Json -Compress),[Text.UTF8Encoding]::new($false))
    }
} catch {
    [IO.File]::WriteAllText($result,(@{Success=$false;UpdatedAt=[DateTimeOffset]::UtcNow.ToString('O');Message=$_.Exception.Message} | ConvertTo-Json -Compress),[Text.UTF8Encoding]::new($false))
    throw
}
