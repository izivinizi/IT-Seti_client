param(
    [switch]$SkipReleaseManifest,
    [string]$OutputBaseFilename = 'ITSeti-Maintenance-Win7-Setup',
    [string]$PackageDirectory = 'dist\Win7\Package',
    [switch]$ReplacePackage
)
$ErrorActionPreference='Stop'
$OutputRoot=Join-Path $PSScriptRoot 'dist\Win7'
$project=Join-Path $PSScriptRoot 'src\ITSeti.Maintenance.Win7.Port\ITSeti.Maintenance.Win7.Port.csproj'
$package=Join-Path $PSScriptRoot $PackageDirectory
$app=Join-Path $package 'App'
$tools=Join-Path $package 'Tools\CrystalDiskInfo'
$diskMarkSource=Join-Path $PSScriptRoot 'Tools\CrystalDiskMark9'
$diskMarkTarget=Join-Path $package 'Tools\CrystalDiskMark9'
$source=Join-Path $PSScriptRoot 'Tools\CrystalDiskInfo9_6_3_Portable'
$diskSpdSource=Join-Path $PSScriptRoot 'Tools\CrystalDiskMark9\CdmResource\DiskSpd'
$diskSpdTarget=Join-Path $package 'Tools\DiskSpd'
$frameworkSource=Join-Path $OutputRoot 'Prerequisites\NDP48-x86-x64-AllOS-ENU.exe'
$frameworkTarget=Join-Path $package 'Prerequisites\NDP48-x86-x64-AllOS-ENU.exe'
$frameworkUrl='https://download.microsoft.com/download/f/3/a/f3a6af84-da23-40a5-8d1c-49cc10c8e76f/NDP48-x86-x64-AllOS-ENU.exe'
$frameworkHash='0A3A390C47E639D0F7FC65B21195FEE6B7F65B066F80F70C60FAB191D14B7E40'
$compiler=Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'
if(!(Test-Path -LiteralPath $compiler -PathType Leaf)){throw 'Inno Setup 6 compiler not found.'}
if($OutputBaseFilename -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$'){throw 'Installer file name is invalid.'}
if([IO.Path]::IsPathRooted($PackageDirectory) -or $PackageDirectory -match '(^|[\\/])\.\.([\\/]|$)'){
    throw 'Package directory must remain inside the Win7 build workspace.'
}
$installer=Join-Path $OutputRoot ($OutputBaseFilename + '.exe')
if(Test-Path -LiteralPath $installer){throw "Installer already exists and was left untouched: $installer"}
if(!(Test-Path -LiteralPath $frameworkSource -PathType Leaf)){
    New-Item -ItemType Directory -Path (Split-Path -Parent $frameworkSource) -Force | Out-Null
    Invoke-WebRequest -Uri $frameworkUrl -OutFile $frameworkSource -ErrorAction Stop
}
if((Get-FileHash -LiteralPath $frameworkSource -Algorithm SHA256).Hash -ne $frameworkHash){
    throw 'Microsoft .NET Framework 4.8 offline installer hash mismatch.'
}
$signature=Get-AuthenticodeSignature -LiteralPath $frameworkSource
if($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'CN=Microsoft Corporation'){
    throw 'Microsoft .NET Framework 4.8 offline installer signature is not valid.'
}
foreach($name in @('DiskInfo32.exe','DiskInfo64.exe','ReadMe.txt')){
    if(!(Test-Path -LiteralPath (Join-Path $source $name) -PathType Leaf)){throw "Missing CrystalDiskInfo file: $name"}
}
foreach($name in @('DiskSpd32L.exe','DiskSpd64L.exe')){
    if(!(Test-Path -LiteralPath (Join-Path $diskSpdSource $name) -PathType Leaf)){throw "Missing Win7 DiskSpd file: $name"}
}
foreach($name in @('DiskMark32.exe','DiskMark64.exe')){
    if(!(Test-Path -LiteralPath (Join-Path $diskMarkSource $name) -PathType Leaf)){throw "Missing Win7 CrystalDiskMark file: $name"}
}
$workspace=[IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\')
$packagePath=[IO.Path]::GetFullPath($package)
$packageParent=[IO.Path]::GetFullPath($OutputRoot).TrimEnd('\')+'\'
if(!$packagePath.StartsWith($packageParent,[StringComparison]::OrdinalIgnoreCase)){
    throw 'Package path is outside the Win7 build directory.'
}
if(Test-Path -LiteralPath $packagePath){
    if(!$ReplacePackage){throw "Package already exists and was left untouched: $packagePath. Use -ReplacePackage only when rebuilding that exact package."}
    Remove-Item -LiteralPath $packagePath -Recurse -Force
}
dotnet publish $project -c Release -o $app -p:NuGetAudit=false
if($LASTEXITCODE -ne 0){throw 'Win7 client publish failed.'}
New-Item -ItemType Directory -Path $tools -Force | Out-Null
Get-ChildItem -LiteralPath $source -Force |
    Where-Object {$_.Name -notin @('Smart','DiskInfo.ini','DiskInfo.txt')} |
    Copy-Item -Destination $tools -Recurse -Force
New-Item -ItemType Directory -Path $diskSpdTarget -Force | Out-Null
foreach($name in @('DiskSpd32L.exe','DiskSpd64L.exe')){
    Copy-Item -LiteralPath (Join-Path $diskSpdSource $name) -Destination (Join-Path $diskSpdTarget $name) -Force
}
New-Item -ItemType Directory -Path $diskMarkTarget -Force | Out-Null
Get-ChildItem -LiteralPath $diskMarkSource -Force | Copy-Item -Destination $diskMarkTarget -Recurse -Force
New-Item -ItemType Directory -Path (Split-Path -Parent $frameworkTarget) -Force | Out-Null
Copy-Item -LiteralPath $frameworkSource -Destination $frameworkTarget -Force
if($PackageDirectory -eq 'dist\Win7\Package'){
    & $compiler (Join-Path $PSScriptRoot 'Installer-Win7.iss') "/F$OutputBaseFilename"
} else {
    $issPath=Join-Path $PSScriptRoot 'Installer-Win7.iss'
    $issText=[IO.File]::ReadAllText($issPath)
    $issText=$issText.Replace('#define PackageRoot "dist\Win7\Package"',('#define PackageRoot "{0}"' -f $PackageDirectory))
    $temporaryIss=Join-Path $PSScriptRoot ('.Installer-Win7-test-{0}.iss' -f [Guid]::NewGuid().ToString('N'))
    [IO.File]::WriteAllText($temporaryIss,$issText,(New-Object System.Text.UTF8Encoding -ArgumentList $false))
    try { & $compiler $temporaryIss "/F$OutputBaseFilename" }
    finally { Remove-Item -LiteralPath $temporaryIss -Force -ErrorAction SilentlyContinue }
}
if($LASTEXITCODE -ne 0){throw 'Win7 installer compilation failed.'}
if(!(Test-Path -LiteralPath $installer -PathType Leaf)){throw 'Installer output missing.'}
Write-Host ('Win7 installer: '+$installer)
Write-Host ('SHA256: '+(Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash)
if ($SkipReleaseManifest) { return }
$projectXml=New-Object System.Xml.XmlDocument
$projectXml.Load($project)
$version=([string]$projectXml.SelectSingleNode('/Project/PropertyGroup/Version').InnerText) -replace '-beta$',''
if($version -notmatch '^\d+\.\d+\.\d+$'){throw "Invalid Win7 application version: $version"}
$tag="v$version-win7-beta"
$manifest=[ordered]@{
    version=$version
    tag=$tag
    assetName='ITSeti-Maintenance-Win7-Setup.exe'
    size=(Get-Item -LiteralPath $installer).Length
    digest='sha256:'+((Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant())
    downloadUrl="https://github.com/izivinizi/IT-Seti_client/releases/download/$tag/ITSeti-Maintenance-Win7-Setup.exe"
}
[IO.File]::WriteAllText((Join-Path (Split-Path $PSScriptRoot -Parent) 'release-win7.json'),
    ($manifest | ConvertTo-Json),[Text.UTF8Encoding]::new($false))
