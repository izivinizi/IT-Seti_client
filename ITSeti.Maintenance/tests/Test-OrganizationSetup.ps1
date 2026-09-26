param([string]$Root='.')
$ErrorActionPreference='Stop'
$rootPath=(Resolve-Path -LiteralPath $Root).Path
$testRoot=Join-Path $env:TEMP ('itseti-org-test-'+[guid]::NewGuid().ToString('N'))
$setup=Join-Path $testRoot 'ITSETI-Setup\system'
try {
    New-Item -ItemType Directory -Path $setup -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $setup 'Install.ps1'),"throw 'must not run'")
    $result=Join-Path $testRoot 'result.txt'
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $rootPath 'Install-OrganizationSoftware.ps1') -InstallerDirectory $testRoot -ResultFile $result -PreflightOnly
    if($LASTEXITCODE -ne 2){throw 'Modified organization installer was not rejected.'}
    $rejection=Get-Content -LiteralPath $result -Raw
    if($rejection -notmatch 'changed since audit'){throw "Rejection reason was not saved: $rejection"}
    $iss=[IO.File]::ReadAllText((Join-Path $rootPath 'Installer.iss'))
    if(([regex]::Matches($iss,'Name: "\{autodesktop\}')).Count -ne 1){throw 'Installer creates more than one desktop shortcut.'}
    if($iss -notmatch 'SoftwareCheck := TNewCheckBox.Create' -or
       $iss -notmatch 'if SoftwareCheck.Checked then' -or
       $iss -notmatch 'Install-OrganizationSoftware.ps1'){
        throw 'Optional organization software step is missing.'
    }
    $install=[IO.File]::ReadAllText((Join-Path $rootPath 'Install-Maintenance.ps1'))
    if($install -notmatch 'if\(\$PreinstalledApp\)\{' -or $install -notmatch 'Remove-Item -LiteralPath \$path'){
        throw 'Legacy shortcuts are not removed during EXE installation.'
    }
    $worker=[IO.File]::ReadAllText((Join-Path $rootPath 'src\ITSeti.Maintenance.Infrastructure\Backend\InstalledOrganizationSetup.ps1'))
    $operationIndex=$worker.IndexOf('$operation=if($payload.Operation)',[StringComparison]::Ordinal)
    $sourceIndex=$worker.IndexOf('$source=if($sourceValue){[IO.Path]::GetFullPath($sourceValue)}',[StringComparison]::Ordinal)
    if($operationIndex -lt 0 -or $sourceIndex -le $operationIndex -or
       $worker -notmatch '\$sourceValue=if\(\$operation -eq ''Install''\)'){
        throw 'Uninstall requests must bypass source-path normalization.'
    }
    'PASS: unsafe package refused; optional installer task and single desktop shortcut present.'
    $global:LASTEXITCODE=0
} finally {
    if(Test-Path -LiteralPath $testRoot){Remove-Item -LiteralPath $testRoot -Recurse -Force}
}
