param([string]$Root='.')
$ErrorActionPreference='Stop'
$rootPath=(Resolve-Path -LiteralPath $Root).Path
$iss=[IO.File]::ReadAllText((Join-Path $rootPath 'Installer.iss'),[Text.Encoding]::UTF8)
$project=[IO.File]::ReadAllText((Join-Path $rootPath 'src\ITSeti.Maintenance.App\ITSeti.Maintenance.App.csproj'),[Text.Encoding]::UTF8)
$install=[IO.File]::ReadAllText((Join-Path $rootPath 'Install-Maintenance.ps1'),[Text.Encoding]::UTF8)
$worker=[IO.File]::ReadAllText((Join-Path $rootPath 'src\ITSeti.Maintenance.Infrastructure\Backend\InstalledOrganizationSetup.ps1'),[Text.Encoding]::UTF8)
if($iss -match 'SoftwareCheck|Setup\\ITSETI-Setup|Install-OrganizationSoftware.ps1'){
    throw 'The application installer still embeds or launches the managed software package.'
}
if($project -match 'Setup\\ITSETI-Setup\\\*\*' -or $install -match 'Copy-Item[^\r\n]+Install-OrganizationSoftware.ps1'){
    throw 'Managed software files must not be copied into the installed application.'
}
if(!$worker.Contains('Join-Path $base ''server-device.json''') -or !$worker.Contains('ServerSoftware.ps1')){
    throw 'The SYSTEM software worker must use the server catalog.'
}
'PASS: managed software is excluded from the application installer and installed only from the server catalog.'
