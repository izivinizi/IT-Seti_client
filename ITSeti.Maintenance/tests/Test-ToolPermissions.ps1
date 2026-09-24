param([string]$Root=(Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference='Stop'
$installer=Get-Content -LiteralPath (Join-Path $Root 'Install-Maintenance.ps1') -Raw
$rootAcl=@($installer -split "`r?`n" | Where-Object {$_ -match '^& icacls\.exe \$securityRoot /inheritance:r'})
if($rootAcl.Count -ne 1 -or $rootAcl[0] -match '/T\b'){throw 'Installer must disable inheritance only on ProgramData root'}
if($installer -match '(?m)^& icacls\.exe .* /T \| Out-Null$'){
    throw 'Installer must not rewrite every file ACL under ProgramData'
}
$inno=Get-Content -LiteralPath (Join-Path $Root 'Installer.iss') -Raw
$worker=Get-Content -LiteralPath (Join-Path $Root 'src\ITSeti.Maintenance.Infrastructure\Backend\InstalledCheck.ps1') -Raw
if($inno -notmatch 'DestDir: "\{app\}\\Tools"' -or $worker -notmatch "Split-Path \`$PSScriptRoot -Parent" -or $installer -match "Join-Path \`$data 'Tools'"){
    throw 'Installed tools must run from the application directory, not ProgramData'
}
Write-Host 'PASS: tool executables are installed under Program Files and ProgramData ACLs are not recursively rewritten.'
