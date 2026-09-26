$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$repository = 'izivinizi/IT-Seti_client'
$assetName = 'ITSeti-Maintenance-Setup.exe'
$appDirectory = Split-Path $PSScriptRoot -Parent
$appPath = Join-Path $appDirectory 'ITSeti.Maintenance.exe'
$appAssemblyPath = Join-Path $appDirectory 'ITSeti.Maintenance.dll'
$updateRoot = Join-Path $env:ProgramData 'ITSeti\Maintenance\Updates'
$statusPath = Join-Path $updateRoot 'last-result.txt'
$logPath = Join-Path $updateRoot 'updater.log'
$partial = Join-Path $updateRoot ($assetName + '.download')

function Set-UpdateStatus([string]$Message) {
    New-Item -ItemType Directory -Path $updateRoot -Force | Out-Null
    $line = '{0:yyyy-MM-dd HH:mm:ss} | {1}' -f (Get-Date), $Message
    $temporary = $statusPath + '.pending'
    [IO.File]::WriteAllText($temporary, $line, [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporary -Destination $statusPath -Force
    Add-Content -LiteralPath $logPath -Value $line -Encoding UTF8
}

try {
    Set-UpdateStatus 'Обновление запущено системной задачей.'
    $systemSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    if ($systemSid -ne 'S-1-5-18') { throw 'Задача обновления должна выполняться от LocalSystem.' }
    if (!(Test-Path -LiteralPath $appPath -PathType Leaf) -or !(Test-Path -LiteralPath $appAssemblyPath -PathType Leaf)) { throw 'Установленное приложение не найдено.' }

    Set-UpdateStatus 'Проверяю выпуск GitHub.'
    $currentText = (Get-Item -LiteralPath $appAssemblyPath).VersionInfo.FileVersion
    $currentVersion = [Version]($currentText -replace '\.\d+$', '')
    $manifestUrl = "https://raw.githubusercontent.com/$repository/main/release.json"
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $release = Invoke-RestMethod -Uri $manifestUrl -Headers @{ 'User-Agent' = 'ITSeti-Maintenance-Updater/0.13.1'; 'Accept' = 'application/json'; 'Cache-Control' = 'no-cache' } -TimeoutSec 30
    $versionText = [string]$release.version
    $tag = [string]$release.tag
    if ($versionText -notmatch '^\d+\.\d+\.\d+(?:\.\d+)?$' -or $tag -cne "v$versionText") { throw 'В манифесте GitHub некорректная версия или тег.' }
    $latestVersion = [Version]$versionText
    if ($latestVersion -le $currentVersion) {
        Set-UpdateStatus "Установлена последняя версия $currentVersion; обновление не требуется."
        exit 0
    }

    $manifestAssetName = [string]$release.assetName
    if ($manifestAssetName -cne $assetName -or [string]$release.size -notmatch '^\d+$') { throw 'В манифесте не указаны проверенные параметры установщика.' }
    $assetSize = [long]$release.size
    if ($assetSize -lt 1 -or $assetSize -gt 500000000) { throw 'Размер установщика в манифесте недопустим.' }
    if ([string]$release.digest -notmatch '^sha256:([0-9a-fA-F]{64})$') { throw 'В манифесте нет SHA-256 установщика.' }
    $expectedHash = $Matches[1].ToUpperInvariant()
    $expectedUrl = "https://github.com/$repository/releases/download/$tag/$assetName"
    $downloadUrl = [string]$release.downloadUrl
    if ($downloadUrl -cne $expectedUrl) { throw 'Ссылка на установщик не соответствует доверенному репозиторию.' }

    New-Item -ItemType Directory -Path $updateRoot -Force | Out-Null
    Set-UpdateStatus "Найдена версия $latestVersion. Жду закрытия приложения."
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
    if ($running) { throw 'Окно приложения не закрылось; обновление отменено.' }

    Set-UpdateStatus 'Скачиваю установщик.'
    Invoke-WebRequest -Uri $downloadUrl -OutFile $partial -UseBasicParsing -TimeoutSec 600
    if ((Get-Item -LiteralPath $partial).Length -ne $assetSize) { throw 'Размер скачанного установщика не совпал с манифестом.' }
    Set-UpdateStatus 'Проверяю SHA-256 установщика.'
    $actualHash = (Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash
    if ($actualHash -cne $expectedHash) { throw 'SHA-256 установщика не совпал с данными GitHub.' }
    $installer = Join-Path $updateRoot $assetName
    Move-Item -LiteralPath $partial -Destination $installer -Force
    Set-UpdateStatus "Устанавливаю версию $latestVersion."
    $process = Start-Process -FilePath $installer -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', '/CLOSEAPPLICATIONS') -PassThru -Wait
    if ($process.ExitCode -ne 0) { throw "Установщик завершился с кодом $($process.ExitCode)." }
    Set-UpdateStatus "Версия $latestVersion установлена. Запустите приложение снова."
} catch {
    $message = $_.Exception.Message.Replace("`r", ' ').Replace("`n", ' ')
    try { Set-UpdateStatus ('Ошибка обновления: ' + $message) } catch { }
    exit 1
} finally {
    Remove-Item -LiteralPath $partial -Force -ErrorAction SilentlyContinue
}
