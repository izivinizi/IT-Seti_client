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
$api = "https://api.github.com/repos/$repository/releases/latest"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$release = Invoke-RestMethod -Uri $api -Headers @{ 'User-Agent' = 'ITSeti-Maintenance-Updater/0.9.2'; 'Accept' = 'application/vnd.github+json' } -TimeoutSec 30
if ($release.draft -or $release.prerelease) { throw 'Latest release is not a stable release.' }
$tag = [string]$release.tag_name
if ($tag -notmatch '^v?(\d+\.\d+\.\d+(?:\.\d+)?)$') { throw 'Release tag is not a supported version.' }
$latestVersion = [Version]$Matches[1]
if ($latestVersion -le $currentVersion) { exit 0 }

$asset = @($release.assets | Where-Object { $_.name -ceq $assetName }) | Select-Object -First 1
if (!$asset -or $asset.state -ne 'uploaded' -or $asset.size -lt 1 -or $asset.size -gt 500000000) { throw 'Release installer is missing or has an invalid size.' }
if ([string]$asset.digest -notmatch '^sha256:([0-9a-fA-F]{64})$') { throw 'Release does not publish a SHA-256 digest.' }
$expectedHash = $Matches[1].ToUpperInvariant()
$encodedTag = [Uri]::EscapeDataString($tag)
$expectedUrl = "https://github.com/$repository/releases/download/$encodedTag/$assetName"
if ([string]$asset.browser_download_url -cne $expectedUrl) { throw 'Release download URL did not match the trusted repository.' }

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
    Invoke-WebRequest -Uri $expectedUrl -OutFile $partial -UseBasicParsing -TimeoutSec 600
    $actualHash = (Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash
    if ($actualHash -cne $expectedHash) { throw 'Installer SHA-256 does not match the public GitHub release metadata.' }
    Move-Item -LiteralPath $partial -Destination $installer -Force
    $process = Start-Process -FilePath $installer -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-','/CLOSEAPPLICATIONS') -PassThru -Wait
    if ($process.ExitCode -ne 0) { throw "Installer exited with code $($process.ExitCode)." }
} finally {
    Remove-Item -LiteralPath $partial -Force -ErrorAction SilentlyContinue
}
