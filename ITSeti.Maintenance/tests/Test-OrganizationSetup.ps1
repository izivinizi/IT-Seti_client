param([string]$Root='.')
$ErrorActionPreference='Stop'
$rootPath=(Resolve-Path -LiteralPath $Root).Path
$testRoot=Join-Path $env:TEMP ('itseti-org-test-'+[guid]::NewGuid().ToString('N'))
$setup=Join-Path $testRoot 'ITSETI-Setup\system'
try {
    $packages=Join-Path $setup 'packages'
    $panel=Join-Path $setup 'panel'
    New-Item -ItemType Directory -Path $packages,$panel -Force | Out-Null
    $names=@('AnyDesk-installer.exe','Host-IT-SETI.RMS.7.7.3.0v3.msi','OCS-Agent-Installerv4.exe','DesktopInfo3230.exe')
    foreach($name in $names){[IO.File]::WriteAllText((Join-Path $packages $name),'unsigned test fixture')}
    foreach($name in @('DesktopInfo.ini','update-support-ids.ps1','start-panel.vbs')){[IO.File]::WriteAllText((Join-Path $panel $name),'tampered test fixture')}
    $result=Join-Path $testRoot 'result.txt'
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $rootPath 'Install-OrganizationSoftware.ps1') -InstallerDirectory $testRoot -ResultFile $result -PreflightOnly
    if($LASTEXITCODE -ne 2){throw 'Modified organization installer was not rejected.'}
    $rejection=Get-Content -LiteralPath $result -Raw
    if($rejection -notmatch 'verification failed' -or !($names | Where-Object { $rejection -match [regex]::Escape($_) })){
        throw 'A tampered package was not rejected before installation.'
    }
    $iss=[IO.File]::ReadAllText((Join-Path $rootPath 'Installer.iss'))
    if(([regex]::Matches($iss,'Name: "\{autodesktop\}')).Count -ne 1){throw 'Installer creates more than one desktop shortcut.'}
    if($iss -notmatch 'SoftwareCheck := TNewCheckBox.Create' -or
       $iss -notmatch 'if SoftwareCheck.Checked then' -or
       $iss -notmatch 'Setup\\ITSETI-Setup' -or $iss -match 'SoftwareDirEdit|BrowseSoftwareDir'){
        throw 'Optional software must install from the package bundled with the application.'
    }
    $install=[IO.File]::ReadAllText((Join-Path $rootPath 'Install-Maintenance.ps1'))
    if($install -notmatch 'if\(\$PreinstalledApp\)\{' -or $install -notmatch 'Remove-Item -LiteralPath \$path'){
        throw 'Legacy shortcuts are not removed during EXE installation.'
    }
    $worker=[IO.File]::ReadAllText((Join-Path $rootPath 'src\ITSeti.Maintenance.Infrastructure\Backend\InstalledOrganizationSetup.ps1'))
    if($worker -notmatch 'Setup\\ITSETI-Setup' -or $worker -match 'payload\.Source'){
        throw 'The system worker must use packaged application files, not an external setup folder.'
    }
    $project=[IO.File]::ReadAllText((Join-Path $rootPath 'src\ITSeti.Maintenance.App\ITSeti.Maintenance.App.csproj'))
    if($project -notmatch 'Setup\\ITSETI-Setup' -or $project -notmatch 'CopyToPublishDirectory') {throw 'ITSETI-Setup payload is not included in app publish output.'}
    'PASS: modified packages rejected; installation payload is bundled with the app and no external folder is needed.'
    $global:LASTEXITCODE=0
} finally {
    if(Test-Path -LiteralPath $testRoot){Remove-Item -LiteralPath $testRoot -Recurse -Force}
}
