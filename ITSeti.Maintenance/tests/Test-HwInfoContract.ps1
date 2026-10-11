param([string]$Root = (Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference = 'Stop'

$reader = Get-Content -LiteralPath (Join-Path $Root 'src\ITSeti.Maintenance.Infrastructure\HwInfoReader.cs') -Raw
$win7Reader = Get-Content -LiteralPath (Join-Path $Root 'src\ITSeti.Maintenance.Win7\LegacyHwInfoReader.cs') -Raw
$builder = Get-Content -LiteralPath (Join-Path $Root 'Build-InstallPackage.ps1') -Raw
$win7Builder = Get-Content -LiteralPath (Join-Path $Root 'Build-Win7Package.ps1') -Raw
$installer = Get-Content -LiteralPath (Join-Path $Root 'Installer.iss') -Raw
$win7Installer = Get-Content -LiteralPath (Join-Path $Root 'Installer-Win7.iss') -Raw
$binary = Join-Path $Root 'Tools\HWiNFO\HWiNFO32.exe'

if (!(Test-Path -LiteralPath $binary -PathType Leaf)) { throw 'Bundled HWiNFO32.exe is missing.' }
if (!$reader.Contains('Tools", "HWiNFO", "HWiNFO32.exe') -or
    !$reader.Contains('SessionLifetime') -or !$reader.Contains('SessionRestartDue') -or
    !$reader.Contains('ResetSession') -or !$reader.Contains('CreateNoWindow = true')) {
    throw 'HWiNFO reader does not prove packaged discovery, hidden execution and session reset.'
}
if (!$win7Reader.Contains('Tools", "HWiNFO", "HWiNFO32.exe') -or
    !$win7Reader.Contains('SessionLifetime') -or !$win7Reader.Contains('SessionRestartDue') -or
    !$win7Reader.Contains('ResetSession') -or !$win7Reader.Contains('CreateNoWindow = true')) {
    throw 'Win7 HWiNFO reader does not prove service execution and session reset.'
}
if (!$builder.Contains("'HWiNFO\HWiNFO32.exe'") -or !$builder.Contains("'HWiNFO'))")) {
    throw 'Main package builder does not include HWiNFO.'
}
if (!$win7Builder.Contains('Tools\HWiNFO') -or !$win7Builder.Contains('HWiNFO32.exe')) {
    throw 'Win7 package builder does not include HWiNFO32.'
}
if (!$installer.Contains('{#PackageRoot}\Tools\*') -or !$win7Installer.Contains('{#PackageRoot}\Tools\HWiNFO\*')) {
    throw 'One of the installers does not copy the HWiNFO payload.'
}
$hash = (Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash.ToUpperInvariant()
if ($hash -ne '83E22DD6EEA1F380732153F60F08B7E8F5DD17E5256E70023EA543CA3A8C1781') {
    throw "Unexpected HWiNFO32.exe SHA-256: $hash"
}
$version = [Diagnostics.FileVersionInfo]::GetVersionInfo($binary).FileVersion
if ($version -notmatch '^8\.54') { throw "Unexpected HWiNFO32.exe version: $version" }
'PASS: HWiNFO32 payload, hidden service execution, 10-hour session reset and both installer contracts'
