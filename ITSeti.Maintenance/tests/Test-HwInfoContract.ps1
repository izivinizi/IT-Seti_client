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
    !$reader.Contains('ResetSession') -or !$reader.Contains('CreateNoWindow = true') -or
    !$reader.Contains('PchTemperature') -or !$reader.Contains('ITSeti.HwInfoBridge.SensorReader.Read()') -or
    $reader.Contains('ArgumentList.Add("-l" + temporary)') -or !$reader.Contains('ParseSensorCsv')) {
    throw 'HWiNFO reader does not prove packaged discovery, hidden execution and session reset.'
}
if (!$win7Reader.Contains('Tools", "HWiNFO", "HWiNFO32.exe') -or
    !$win7Reader.Contains('SessionLifetime') -or !$win7Reader.Contains('SessionRestartDue') -or
    !$win7Reader.Contains('ResetSession') -or !$win7Reader.Replace(' ','').Contains('CreateNoWindow=true') -or
    !$win7Reader.Contains('PchTemperatureC') -or !$win7Reader.Contains('ITSeti.HwInfoBridge.SensorReader.Read()') -or
    $win7Reader.Contains('-poll_rate=1000') -or !$win7Reader.Contains('ParseCsv')) {
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
$helper = Join-Path $Root 'src\ITSeti.Maintenance.Infrastructure\Backend\HWiNFO.ps1'
$summary = Join-Path $Root 'src\ITSeti.Maintenance.Infrastructure\Backend\Summary.ps1'
$helperSource = Get-Content -LiteralPath $helper -Raw
$summarySource = Get-Content -LiteralPath $summary -Raw
if ($helperSource -match '\[double\]::NaN\(' -or
    !$helperSource.Contains('[double]::IsNaN($value)') -or !$helperSource.Contains('[double]::IsInfinity($value)') -or
    !$summarySource.Contains('function Get-ActiveUptimeHours($OperatingSystem)') -or
    !$summarySource.Contains('Get-ActiveUptimeHours $os')) {
    throw 'Live HWiNFO validation or active-uptime calculation is missing or unsafe.'
}
$previousProgramData = $env:ProgramData
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('ITSeti-HwInfo-' + [Guid]::NewGuid().ToString('N'))
try {
    $env:ProgramData = $fixtureRoot
    $cachePath = Join-Path $fixtureRoot 'ITSeti\Maintenance\hwinfo.json'
    New-Item -ItemType Directory -Path (Split-Path -Parent $cachePath) -Force | Out-Null
    [IO.File]::WriteAllText($cachePath, (@{CapturedAtUtc=[DateTimeOffset]::UtcNow.ToString('O');CpuLoadPercent=42.5;CpuTemperatureC=61}|ConvertTo-Json -Compress), [Text.Encoding]::UTF8)
    . $helper
    $load = Get-HwInfoCpuLoadPercent
    $temperature = Get-HwInfoCpuTemperatureC
    if ($load -ne 42.5 -or $temperature -ne 61) { throw "Fresh numeric HWiNFO values were rejected (load=$load; temperature=$temperature)." }
    [IO.File]::WriteAllText($cachePath, (@{CapturedAtUtc=[DateTimeOffset]::UtcNow.ToString('O');CpuLoadPercent='NaN';CpuTemperatureC=61}|ConvertTo-Json -Compress), [Text.Encoding]::UTF8)
    if ($null -ne (Get-HwInfoCpuLoadPercent)) { throw 'Non-finite HWiNFO load was accepted.' }
} finally {
    if ($null -eq $previousProgramData) { Remove-Item Env:ProgramData -ErrorAction SilentlyContinue } else { $env:ProgramData = $previousProgramData }
    Remove-Item -LiteralPath $fixtureRoot -Recurse -Force -ErrorAction SilentlyContinue
}
'PASS: HWiNFO32 payload, Shared Memory sensor reading, hidden service execution, 10-hour session reset and both installer contracts'
