$ErrorActionPreference='Continue'
if(!([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){
    throw 'Run uninstallation as administrator.'
}
$updatePolicy=Join-Path $PSScriptRoot 'Backend\Set-WindowsAutomaticUpdates.ps1'
if(Test-Path -LiteralPath $updatePolicy){try {& $updatePolicy -Action Restore | Out-Null} catch {Write-Warning ('Не удалось восстановить прежнюю политику автообновлений: '+$_.Exception.Message)}}
& schtasks.exe /Delete /TN 'ITSeti-Maintenance-Full' /F | Out-Null
& schtasks.exe /Delete /TN 'ITSeti-Maintenance-QuickFull' /F | Out-Null
& schtasks.exe /Delete /TN 'ITSeti-Maintenance-Temperature' /F | Out-Null
& schtasks.exe /Delete /TN 'ITSeti-Maintenance-FullRepair' /F | Out-Null
& schtasks.exe /Delete /TN 'ITSeti-Maintenance-Repair' /F | Out-Null
& schtasks.exe /Delete /TN 'ITSeti-Maintenance-Cleanup' /F | Out-Null
& schtasks.exe /Delete /TN 'ITSeti-Maintenance-AutoFullRepair' /F | Out-Null
& schtasks.exe /Delete /TN 'ITSeti-Maintenance-DisableUpdates' /F | Out-Null
& schtasks.exe /Delete /TN 'ITSeti-Maintenance-RestoreUpdates' /F | Out-Null
& schtasks.exe /Delete /TN 'ITSeti-Maintenance-Update' /F | Out-Null
Remove-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' -Name 'ITSeti-Maintenance-Quick' -ErrorAction SilentlyContinue
$data=Join-Path $env:ProgramData 'ITSeti\Maintenance'
Remove-Item -LiteralPath (Join-Path $data 'installed.flag') -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path $data 'Tools') -Recurse -Force -ErrorAction SilentlyContinue
foreach($path in @((Join-Path $env:PUBLIC 'Desktop\ITSeti Maintenance.lnk'),(Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\ITSeti Maintenance.lnk'))){
    Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
}
