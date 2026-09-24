param([string]$PackageRoot=$PSScriptRoot,[string]$InstallRoot=(Join-Path $env:ProgramFiles 'ITSeti Maintenance'),[switch]$PreinstalledApp,[string]$ResultFile='')
$ErrorActionPreference='Stop'
trap {
    if($ResultFile){[IO.File]::WriteAllText($ResultFile,$_.Exception.ToString(),[Text.Encoding]::UTF8)}
    exit 1
}
$admin=([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if(!$admin){throw 'Run this installer once as administrator.'}
$sourceApp=if($PreinstalledApp){$InstallRoot}else{Join-Path $PackageRoot 'App'}
$sourceTools=if($PreinstalledApp){Join-Path $InstallRoot 'Tools'}else{Join-Path $PackageRoot 'Tools'}
if(!(Test-Path -LiteralPath (Join-Path $sourceApp 'ITSeti.Maintenance.exe') -PathType Leaf)){throw 'Published application is missing.'}
foreach($relative in @('CrystalDiskInfo9_6_3_Portable\DiskInfo64.exe','CrystalDiskMark9\CdmResource\DiskSpd\DiskSpd64.exe')){
    if(!(Test-Path -LiteralPath (Join-Path $sourceTools $relative) -PathType Leaf)){throw "Tool is missing: $relative"}
}
$install=$InstallRoot
$data=Join-Path $env:ProgramData 'ITSeti\Maintenance'
New-Item -ItemType Directory -Path $install,$data,(Join-Path $data 'Runs'),(Join-Path $data 'Repairs'),(Join-Path $data 'CleanupRequests'),(Join-Path $data 'CleanupRuns'),(Join-Path $data 'WindowsUpdate') -Force | Out-Null
if(!$PreinstalledApp){Get-ChildItem -LiteralPath $sourceApp -Force | Copy-Item -Destination $install -Recurse -Force}
if(!$PreinstalledApp){
    foreach($folder in @('CrystalDiskInfo9_6_3_Portable','CrystalDiskMark9','TreeSize Free')){
        $source=Join-Path $sourceTools $folder
        if(Test-Path -LiteralPath $source -PathType Container){
            $target=Join-Path (Join-Path $install 'Tools') $folder
            New-Item -ItemType Directory -Path $target -Force | Out-Null
            Get-ChildItem -LiteralPath $source -Force | Copy-Item -Destination $target -Recurse -Force
        }
    }
}
$securityRoot=Join-Path $env:ProgramData 'ITSeti'
& icacls.exe $securityRoot /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-32-545:(OI)(CI)RX' | Out-Null
if($LASTEXITCODE -ne 0){throw 'Could not secure ProgramData root.'}
$cleanupQueue=Join-Path $data 'CleanupRequests'
& icacls.exe $cleanupQueue /grant:r '*S-1-5-32-545:(OI)(CI)M' | Out-Null
if($LASTEXITCODE -ne 0){throw 'Could not allow cleanup requests.'}
$cleanupRuns=Join-Path $data 'CleanupRuns'
& icacls.exe $cleanupRuns /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-32-545:(OI)(CI)RX' | Out-Null
if($LASTEXITCODE -ne 0){throw 'Could not secure cleanup results.'}
$inventory=Join-Path $data 'inventory.txt'
if(!(Test-Path -LiteralPath $inventory -PathType Leaf)){[IO.File]::WriteAllText($inventory,'',[Text.Encoding]::ASCII)}
& icacls.exe $inventory /grant '*S-1-5-32-545:M' | Out-Null
if($LASTEXITCODE -ne 0){throw 'Could not configure inventory number access.'}
$taskName='ITSeti-Maintenance-Full'
$worker=Join-Path $install 'Backend\InstalledCheck.ps1'
$taskTrigger=New-ScheduledTaskTrigger -Once -At (Get-Date).AddYears(20)
$quickTrigger=New-ScheduledTaskTrigger -Daily -DaysInterval 14 -At ([DateTime]::Today.AddHours(3))
$autoFullTrigger=New-ScheduledTaskTrigger -Daily -DaysInterval 60 -At ([DateTime]::Today.AddHours(4))
$taskSettings=New-ScheduledTaskSettingsSet -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Hours 2)
$updateTaskSettings=New-ScheduledTaskSettingsSet -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Minutes 15) -MultipleInstances IgnoreNew
$scheduler=New-Object -ComObject Schedule.Service
$scheduler.Connect()
foreach($name in @($taskName,'ITSeti-Maintenance-QuickFull','ITSeti-Maintenance-AutoFullRepair','ITSeti-Maintenance-Repair','ITSeti-Maintenance-Cleanup','ITSeti-Maintenance-DisableUpdates','ITSeti-Maintenance-RestoreUpdates','ITSeti-Maintenance-Update')){
    $script=if($name -eq 'ITSeti-Maintenance-Repair'){Join-Path $install 'Backend\InstalledRepair.ps1'}elseif($name -eq 'ITSeti-Maintenance-Cleanup'){Join-Path $install 'Backend\InstalledCleanup.ps1'}elseif($name -eq 'ITSeti-Maintenance-Update'){Join-Path $install 'Backend\Update-Application.ps1'}elseif($name -match 'Updates$'){Join-Path $install 'Backend\Set-WindowsAutomaticUpdates.ps1'}else{$worker}
    $arguments='-NoProfile -ExecutionPolicy Bypass -File "'+$script+'"'
    if($name -eq 'ITSeti-Maintenance-QuickFull'){$arguments+=' -Quick'}
    if($name -eq 'ITSeti-Maintenance-AutoFullRepair'){$arguments+=' -StartRepair'}
    if($name -eq 'ITSeti-Maintenance-DisableUpdates'){$arguments+=' -Action Disable'}
    if($name -eq 'ITSeti-Maintenance-RestoreUpdates'){$arguments+=' -Action Restore'}
    $trigger=if($name -eq 'ITSeti-Maintenance-QuickFull'){$quickTrigger}elseif($name -eq 'ITSeti-Maintenance-AutoFullRepair'){$autoFullTrigger}else{$taskTrigger}
    $taskAction=New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $arguments
    $settings=if($name -eq 'ITSeti-Maintenance-Update'){$updateTaskSettings}else{$taskSettings}
    Register-ScheduledTask -TaskName $name -Action $taskAction -Trigger $trigger -Settings $settings -User 'SYSTEM' -RunLevel Highest -Force | Out-Null
    $task=$scheduler.GetFolder('\').GetTask($name)
    $task.SetSecurityDescriptor('D:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;GRGX;;;BU)',0)
}
$legacyTask=Get-ScheduledTask -TaskName 'ITSeti-Maintenance-FullRepair' -ErrorAction SilentlyContinue
if($legacyTask){Unregister-ScheduledTask -TaskName 'ITSeti-Maintenance-FullRepair' -Confirm:$false}
$runKey='HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run'
New-ItemProperty -Path $runKey -Name 'ITSeti-Maintenance-Quick' -Value ('"'+(Join-Path $install 'ITSeti.Maintenance.exe')+'" --scheduled-quick') -PropertyType String -Force | Out-Null
$legacyShortcuts=@((Join-Path $env:PUBLIC 'Desktop\ITSeti Maintenance.lnk'),(Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\ITSeti Maintenance.lnk'))
if($PreinstalledApp){
    foreach($path in $legacyShortcuts){Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue}
}else{
    $shell=New-Object -ComObject WScript.Shell
    foreach($path in $legacyShortcuts){
        $shortcut=$shell.CreateShortcut($path)
        $shortcut.TargetPath=Join-Path $install 'ITSeti.Maintenance.exe'
        $shortcut.WorkingDirectory=$install
        $shortcut.IconLocation=(Join-Path $install 'ITSeti.Maintenance.exe')+',0'
        $shortcut.Save()
    }
}
[IO.File]::WriteAllText((Join-Path $data 'installed.flag'),'ITSeti-Maintenance-Full',[Text.Encoding]::ASCII)
if(!(Test-Path -LiteralPath (Join-Path $data 'installed.flag') -PathType Leaf)){throw 'Installation marker was not written.'}
Write-Host "Installed to $install. Users may launch the application without elevation."
