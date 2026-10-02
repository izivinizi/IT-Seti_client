$ErrorActionPreference='Stop'
$OutputRoot=Join-Path $PSScriptRoot 'dist\Win7'
$project=Join-Path $PSScriptRoot 'src\ITSeti.Maintenance.Win7\ITSeti.Maintenance.Win7.csproj'
$package=Join-Path $OutputRoot 'Package'
$app=Join-Path $package 'App'
$tools=Join-Path $package 'Tools\CrystalDiskInfo'
$source=Join-Path $PSScriptRoot 'Tools\CrystalDiskInfo9_6_3_Portable'
$compiler=Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'
if(!(Test-Path -LiteralPath $compiler -PathType Leaf)){throw 'Inno Setup 6 compiler not found.'}
foreach($name in @('DiskInfo32.exe','DiskInfo64.exe','ReadMe.txt')){
    if(!(Test-Path -LiteralPath (Join-Path $source $name) -PathType Leaf)){throw "Missing CrystalDiskInfo file: $name"}
}
$workspace=[IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\')
$packagePath=[IO.Path]::GetFullPath($package)
$expectedPackage=[IO.Path]::GetFullPath((Join-Path $workspace 'dist\Win7\Package'))
if($packagePath -ne $expectedPackage -or !$packagePath.StartsWith($workspace+'\',[StringComparison]::OrdinalIgnoreCase)){
    throw 'Package path is outside the Win7 build directory.'
}
if(Test-Path -LiteralPath $packagePath){Remove-Item -LiteralPath $packagePath -Recurse -Force}
dotnet publish $project -c Release -o $app -p:NuGetAudit=false
if($LASTEXITCODE -ne 0){throw 'Win7 client publish failed.'}
New-Item -ItemType Directory -Path $tools -Force | Out-Null
Get-ChildItem -LiteralPath $source -Force |
    Where-Object {$_.Name -notin @('Smart','DiskInfo.ini','DiskInfo.txt')} |
    Copy-Item -Destination $tools -Recurse -Force
& $compiler (Join-Path $PSScriptRoot 'Installer-Win7.iss')
if($LASTEXITCODE -ne 0){throw 'Win7 installer compilation failed.'}
$installer=Join-Path $OutputRoot 'ITSeti-Maintenance-Win7-Setup.exe'
if(!(Test-Path -LiteralPath $installer -PathType Leaf)){throw 'Installer output missing.'}
Write-Host ('Win7 installer: '+$installer)
Write-Host ('SHA256: '+(Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash)
