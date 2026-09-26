param([string]$ToolsRoot=(Join-Path $PSScriptRoot 'Tools'),[string]$OutputRoot=(Join-Path $PSScriptRoot 'dist\ITSeti-Maintenance'),[switch]$SkipReleaseManifest)
$ErrorActionPreference='Stop'
& (Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe') -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'tests\Test-PowerShellCompatibility.ps1') -Root $PSScriptRoot
if($LASTEXITCODE -ne 0){throw 'Windows PowerShell compatibility check failed.'}
$required=@('CrystalDiskInfo9_6_3_Portable\DiskInfo64.exe','CrystalDiskMark9\CdmResource\DiskSpd\DiskSpd64.exe','PawnIO\PawnIO_setup.exe')
foreach($relative in $required){if(!(Test-Path -LiteralPath (Join-Path $ToolsRoot $relative) -PathType Leaf)){throw "Missing: $relative"}}
if(!(Test-Path -LiteralPath (Join-Path $ToolsRoot 'Tools.rar') -PathType Leaf)){throw 'Missing: Tools.rar'}
$appProject=Join-Path $PSScriptRoot 'src\ITSeti.Maintenance.App\ITSeti.Maintenance.App.csproj'
dotnet restore $appProject -r win-x64 -p:NuGetAudit=false
if($LASTEXITCODE -ne 0){
    $cache=Join-Path $env:USERPROFILE '.nuget\packages'
    Write-Warning 'NuGet недоступен; повторяем восстановление из локального кеша.'
    dotnet restore $appProject -r win-x64 --source $cache -p:NuGetAudit=false
    if($LASTEXITCODE -ne 0){throw 'dotnet restore failed; verify the NuGet connection or local package cache.'}
}
dotnet publish $appProject -c Release -r win-x64 --self-contained true --no-restore -o (Join-Path $OutputRoot 'App')
if($LASTEXITCODE -ne 0){throw 'dotnet publish failed.'}
$accessControlPackage=Join-Path $env:USERPROFILE '.nuget\packages\system.threading.accesscontrol\10.0.3\runtimes\win\lib\net9.0\System.Threading.AccessControl.dll'
$publishedAccessControl=Join-Path (Join-Path $OutputRoot 'App') 'System.Threading.AccessControl.dll'
if(!(Test-Path -LiteralPath $accessControlPackage -PathType Leaf)){throw 'Restored System.Threading.AccessControl 10.0.3 package is missing.'}
$dependencyVersion=[Reflection.AssemblyName]::GetAssemblyName($accessControlPackage).Version
if($dependencyVersion -lt [Version]'10.0.0.0'){throw "Unexpected System.Threading.AccessControl version: $dependencyVersion"}
Copy-Item -LiteralPath $accessControlPackage -Destination $publishedAccessControl -Force
if([Reflection.AssemblyName]::GetAssemblyName($publishedAccessControl).Version -ne $dependencyVersion){throw 'Published temperature dependency does not match LibreHardwareMonitor.'}
$destination=Join-Path $OutputRoot 'Tools'
New-Item -ItemType Directory -Path $destination -Force | Out-Null
foreach($folder in @('CrystalDiskInfo9_6_3_Portable','CrystalDiskMark9','TreeSize Free','PawnIO','Software')) {
    $source=Join-Path $ToolsRoot $folder
    if(Test-Path -LiteralPath $source -PathType Container){
        $target=Join-Path $destination $folder
        $outputFull=[IO.Path]::GetFullPath($OutputRoot).TrimEnd('\')+'\'
        $targetFull=[IO.Path]::GetFullPath($target)
        if(!$targetFull.StartsWith($outputFull,[StringComparison]::OrdinalIgnoreCase)){throw "Unsafe package target: $targetFull"}
        if(Test-Path -LiteralPath $target){Remove-Item -LiteralPath $target -Recurse -Force}
        New-Item -ItemType Directory -Path $target -Force | Out-Null
        Get-ChildItem -LiteralPath $source -Force |
            Where-Object { $folder -ne 'CrystalDiskInfo9_6_3_Portable' -or $_.Name -notin @('Smart','DiskInfo.ini','DiskInfo.txt') } |
            Copy-Item -Destination $target -Recurse -Force
    }
}
Copy-Item -LiteralPath (Join-Path $ToolsRoot 'Tools.rar') -Destination (Join-Path $destination 'Tools.rar') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install-Maintenance.ps1') -Destination $OutputRoot
$compiler=Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'
if(!(Test-Path -LiteralPath $compiler)){throw 'Install Inno Setup 6 before building the EXE installer.'}
& $compiler (Join-Path $PSScriptRoot 'Installer.iss')
if($LASTEXITCODE -ne 0){throw 'Installer compilation failed.'}
foreach($name in @('Install-ITSeti.ps1','Install-ITSeti-Admin.ps1','Install-ITSeti.cmd')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $PSScriptRoot 'dist') -Force
}
$installerPath=Join-Path $PSScriptRoot 'dist\ITSeti-Maintenance-Setup.exe'
Write-Host "Installer ready: $installerPath"
if(!$SkipReleaseManifest){
    $project=New-Object System.Xml.XmlDocument
    $project.Load((Join-Path $PSScriptRoot 'src\ITSeti.Maintenance.App\ITSeti.Maintenance.App.csproj'))
    $version=[string]$project.SelectSingleNode('/Project/PropertyGroup/Version').InnerText
    if($version -notmatch '^\d+\.\d+\.\d+(?:\.\d+)?$'){throw "Invalid application version: $version"}
    $installer=Get-Item -LiteralPath $installerPath
    $digest=(Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $manifest=[ordered]@{
        version=$version
        tag="v$version"
        assetName='ITSeti-Maintenance-Setup.exe'
        size=$installer.Length
        digest="sha256:$digest"
        downloadUrl="https://github.com/izivinizi/IT-Seti_client/releases/download/v$version/ITSeti-Maintenance-Setup.exe"
    }
    $manifestPath=Join-Path (Split-Path $PSScriptRoot -Parent) 'release.json'
    $manifestJson=$manifest | ConvertTo-Json
    $utf8=New-Object System.Text.UTF8Encoding -ArgumentList $false
    [IO.File]::WriteAllText($manifestPath,$manifestJson,$utf8)
    Write-Host "Release manifest updated: $manifestPath"
}else{
    Write-Host 'Release manifest left unchanged for this local build.'
}
