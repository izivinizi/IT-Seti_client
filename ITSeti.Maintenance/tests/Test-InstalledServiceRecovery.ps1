param([Parameter(Mandatory=$true)][string]$OutputPath)
$ErrorActionPreference='Stop'
$serviceName='ITSetiMaintenanceService'
$taskName='ITSeti-Maintenance-ServiceHealth'
$stopped=$false
try {
    $principal=[Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    if(!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Administrator required.'}
    $statePath=Join-Path $env:ProgramData 'ITSeti\Maintenance\Service\status.json'
    $state=Get-Content -LiteralPath $statePath -Raw -Encoding UTF8 | ConvertFrom-Json
    if(@($state.runningOperations).Count -gt 0){throw 'Active service work must finish before recovery test.'}
    $task=Get-ScheduledTask -TaskName $taskName -ErrorAction Stop
    $taskSid=if($task.Principal.UserId -eq 'S-1-5-18'){'S-1-5-18'}else{([Security.Principal.NTAccount]$task.Principal.UserId).Translate([Security.Principal.SecurityIdentifier]).Value}
    if($taskSid -ne 'S-1-5-18'){throw 'Recovery task must run as SYSTEM.'}
    if(@($task.Triggers).Count -lt 2){throw 'Boot and periodic recovery triggers are required.'}
    Stop-Service -Name $serviceName -ErrorAction Stop
    $stopped=$true
    Start-ScheduledTask -TaskName $taskName -ErrorAction Stop
    $deadline=[DateTime]::UtcNow.AddSeconds(60)
    do {
        Start-Sleep -Milliseconds 500
        $service=Get-Service -Name $serviceName
    } while($service.Status -ne 'Running' -and [DateTime]::UtcNow -lt $deadline)
    if($service.Status -ne 'Running'){throw 'SYSTEM recovery task did not restart service within 60 seconds.'}
    $config=Get-CimInstance Win32_Service -Filter "Name='$serviceName'"
    if($config.StartMode -ne 'Auto' -or $config.StartName -ne 'LocalSystem'){throw 'Invalid service startup configuration.'}
    $result=@{Success=$true;ServiceState=[string]$service.Status;StartMode=$config.StartMode;StartName=$config.StartName;Triggers=@($task.Triggers).Count;VerifiedAt=[DateTimeOffset]::UtcNow.ToString('O')}
} catch {$result=@{Success=$false;Error=$_.Exception.Message}}
finally {
    if($stopped -and (Get-Service -Name $serviceName).Status -ne 'Running'){Start-Service -Name $serviceName}
}
[IO.File]::WriteAllText($OutputPath,($result|ConvertTo-Json),[Text.UTF8Encoding]::new($false))
if(!$result.Success){exit 1}
