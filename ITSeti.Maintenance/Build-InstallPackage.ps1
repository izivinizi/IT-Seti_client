param([string]$ToolsRoot=(Join-Path $PSScriptRoot 'Tools'),[string]$OutputRoot=(Join-Path $PSScriptRoot 'dist\ITSeti-Maintenance'))
$ErrorActionPreference='Stop'
$required=@('CrystalDiskInfo9_6_3_Portable\DiskInfo64.exe','CrystalDiskMark9\CdmResource\DiskSpd\DiskSpd64.exe')
foreach($relative in $required){if(!(Test-Path -LiteralPath (Join-Path $ToolsRoot $relative) -PathType Leaf)){throw "Missing: $relative"}}
dotnet publish (Join-Path $PSScriptRoot 'src\ITSeti.Maintenance.App\ITSeti.Maintenance.App.csproj') -c Release -r win-x64 --self-contained true -o (Join-Path $OutputRoot 'App')
if($LASTEXITCODE -ne 0){throw 'dotnet publish failed.'}
$destination=Join-Path $OutputRoot 'Tools'
New-Item -ItemType Directory -Path $destination -Force | Out-Null
foreach($folder in @('CrystalDiskInfo9_6_3_Portable','CrystalDiskMark9','TreeSize Free')) {
    $source=Join-Path $ToolsRoot $folder
    if(Test-Path -LiteralPath $source -PathType Container){
        $target=Join-Path $destination $folder
        New-Item -ItemType Directory -Path $target -Force | Out-Null
        Get-ChildItem -LiteralPath $source -Force | Copy-Item -Destination $target -Recurse -Force
    }
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install-Maintenance.ps1') -Destination $OutputRoot
$compiler=Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'
if(!(Test-Path -LiteralPath $compiler)){throw 'Install Inno Setup 6 before building the EXE installer.'}
& $compiler (Join-Path $PSScriptRoot 'Installer.iss')
if($LASTEXITCODE -ne 0){throw 'Installer compilation failed.'}
foreach($name in @('Install-ITSeti.ps1','Install-ITSeti-Admin.ps1','Install-ITSeti.cmd')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $PSScriptRoot 'dist') -Force
}
Write-Host "Installer ready: $(Join-Path $PSScriptRoot 'dist\ITSeti-Maintenance-Setup.exe')"
