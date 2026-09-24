param([string]$Root=(Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference='Stop'
foreach($name in @('Uninstall-Options.ps1','OrganizationUninstall.ps1')){
    $path=Join-Path $Root $name
    $tokens=$null;$errors=$null
    [void][System.Management.Automation.Language.Parser]::ParseFile($path,[ref]$tokens,[ref]$errors)
    if($errors){throw "$name has parser errors: $($errors -join '; ')"}
}
$installer=Get-Content -LiteralPath (Join-Path $Root 'Installer.iss') -Raw
foreach($name in @('Uninstall-Options.ps1','OrganizationUninstall.ps1','Uninstall-ITSeti.cmd')){
    if(!$installer.Contains("Source: `"$name`"; DestDir: `"{app}`"")){throw "$name is not packaged"}
}
if(([regex]::Matches($installer,'Name: "\{autodesktop\}')).Count -ne 1){throw 'Expected one desktop shortcut'}
$options=Get-Content -LiteralPath (Join-Path $Root 'Uninstall-Options.ps1') -Raw
if(!$options.Contains('unins000.exe') -or !$options.Contains('OrganizationUninstall.ps1') -or !$options.Contains("if(`$software.ExitCode -eq 3){exit 0}")){
    throw 'Uninstall choice contract missing'
}
Write-Host 'PASS: uninstall scripts parse; both variants are packaged; one desktop shortcut.'
