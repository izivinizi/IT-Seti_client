$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$repository = 'izivinizi/IT-Seti_client'
$assetName = 'ITSeti-Maintenance-Setup.exe'
$appDirectory = Split-Path $PSScriptRoot -Parent
$appPath = Join-Path $appDirectory 'ITSeti.Maintenance.exe'
$appAssemblyPath = Join-Path $appDirectory 'ITSeti.Maintenance.dll'
$systemSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
if ($systemSid -ne 'S-1-5-18') { throw 'Application updater must run as LocalSystem.' }
if (!(Test-Path -LiteralPath $appPath -PathType Leaf) -or !(Test-Path -LiteralPath $appAssemblyPath -PathType Leaf)) { throw 'Installed application was not found.' }

$currentText = (Get-Item -LiteralPath $appAssemblyPath).VersionInfo.FileVersion
$currentVersion = [Version]($currentText -replace '\.\d+$', '')
$manifestUrl = "https://github.com/$repository/releases/latest/download/release.json"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$release = Invoke-RestMethod -Uri $manifestUrl -Headers @{ 'User-Agent' = 'ITSeti-Maintenance-Updater/0.9.6'; 'Accept' = 'application/json'; 'Cache-Control' = 'no-cache' } -TimeoutSec 30
$versionText = [string]$release.version
$tag = [string]$release.tag
if ($versionText -notmatch '^\d+\.\d+\.\d+(?:\.\d+)?$' -or $tag -cne "v$versionText") { throw 'Update manifest contains an invalid version or tag.' }
$latestVersion = [Version]$versionText
if ($latestVersion -le $currentVersion) { exit 0 }

$manifestAssetName = [string]$release.assetName
if ($manifestAssetName -cne $assetName -or [string]$release.size -notmatch '^\d+$') { throw 'Update manifest has no valid installer size.' }
$assetSize = [long]$release.size
if ($assetSize -lt 1 -or $assetSize -gt 500000000) { throw 'Update manifest has no valid installer size.' }
if ([string]$release.digest -notmatch '^sha256:([0-9a-fA-F]{64})$') { throw 'Update manifest has no valid SHA-256 digest.' }
$expectedHash = $Matches[1].ToUpperInvariant()
$expectedUrl = "https://github.com/$repository/releases/download/$tag/$assetName"
$downloadUrl = [string]$release.downloadUrl
if ($downloadUrl -cne $expectedUrl) { throw 'Release download URL did not match the trusted repository.' }

$deadline = [DateTime]::UtcNow.AddMinutes(4)
do {
    $running = $false
    foreach ($process in Get-Process -Name 'ITSeti.Maintenance' -ErrorAction SilentlyContinue) {
        try {
            if ([string]::Equals($process.Path, $appPath, [StringComparison]::OrdinalIgnoreCase)) { $running = $true; break }
        } catch { $running = $true; break }
    }
    if ($running) { Start-Sleep -Seconds 2 }
} while ($running -and [DateTime]::UtcNow -lt $deadline)
if ($running) { throw 'Application did not close; update cancelled.' }

$updateRoot = Join-Path $env:ProgramData 'ITSeti\Maintenance\Updates'
New-Item -ItemType Directory -Path $updateRoot -Force | Out-Null
$installer = Join-Path $updateRoot 'ITSeti-Maintenance-Setup.exe'
$partial = $installer + '.download'
try {
    Invoke-WebRequest -Uri $downloadUrl -OutFile $partial -UseBasicParsing -TimeoutSec 600
    if ((Get-Item -LiteralPath $partial).Length -ne $assetSize) { throw 'Downloaded installer size does not match the release manifest.' }
    $actualHash = (Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash
    if ($actualHash -cne $expectedHash) { throw 'Installer SHA-256 does not match the public GitHub release metadata.' }
    Move-Item -LiteralPath $partial -Destination $installer -Force
    $process = Start-Process -FilePath $installer -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-','/CLOSEAPPLICATIONS') -PassThru -Wait
    if ($process.ExitCode -ne 0) { throw "Installer exited with code $($process.ExitCode)." }
} finally {
    Remove-Item -LiteralPath $partial -Force -ErrorAction SilentlyContinue
}
