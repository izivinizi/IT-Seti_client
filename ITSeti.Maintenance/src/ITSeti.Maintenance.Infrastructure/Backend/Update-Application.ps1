$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
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

function ConvertTo-AppVersion([string]$Text) {
    if($Text -notmatch '^\d+\.\d+\.\d+(?:\.\d+)?$'){throw 'Неверный формат версии установленного приложения.'}
    $parsed=[Version]$Text
    return [Version]::new($parsed.Major,$parsed.Minor,$parsed.Build,[Math]::Max($parsed.Revision,0))
}

try {
    Set-UpdateStatus 'Обновление запущено системной задачей.'
    $systemSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    if ($systemSid -ne 'S-1-5-18') { throw 'Задача обновления должна выполняться от LocalSystem.' }
    if (!(Test-Path -LiteralPath $appPath -PathType Leaf) -or !(Test-Path -LiteralPath $appAssemblyPath -PathType Leaf)) { throw 'Установленное приложение не найдено.' }

    Set-UpdateStatus 'Проверяю каталог обновлений на сервере.'
    $currentText = (Get-Item -LiteralPath $appAssemblyPath).VersionInfo.FileVersion
    $currentVersion = ConvertTo-AppVersion $currentText
    $deviceFile = Join-Path $env:ProgramData 'ITSeti\Maintenance\server-device.json'
    if (!(Test-Path -LiteralPath $deviceFile -PathType Leaf)) { throw 'Данные подключения к серверу не найдены.' }
    $device = Get-Content -LiteralPath $deviceFile -Raw -Encoding UTF8 | ConvertFrom-Json
    if (!$device.serverUrl -or !$device.deviceId -or !$device.deviceKey) { throw 'Данные подключения к серверу неполны.' }
    $serverBase = [Uri]$device.serverUrl
    if ($serverBase.Scheme -ne 'https') { throw 'Сервер обновлений должен использовать HTTPS.' }
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $updateUrl = New-Object Uri($serverBase, '/api/v1/application-update?platform=windows')
    $headers = @{ 'User-Agent' = 'ITSeti-Maintenance-Updater/2.4.9'; 'Accept' = 'application/json'; 'Cache-Control' = 'no-cache'; 'X-Device-Id' = [string]$device.deviceId; 'X-Device-Key' = [string]$device.deviceKey }
    $release = Invoke-RestMethod -Uri $updateUrl -Headers $headers -TimeoutSec 30
    if ($null -eq $release) {
        Set-UpdateStatus "Установлена последняя версия $currentVersion; обновление не требуется."
        exit 0
    }
    $versionText = [string]$release.version
    if ($versionText -notmatch '^\d+\.\d+\.\d+(?:\.\d+)?$' -or [string]$release.key -cne 'application' -or [string]$release.platform -cne 'windows') { throw 'Сервер вернул некорректные параметры обновления.' }
    $latestVersion = ConvertTo-AppVersion $versionText
    if ($latestVersion -le $currentVersion) {
        Set-UpdateStatus "Установлена последняя версия $currentVersion; обновление не требуется."
        exit 0
    }

    $manifestAssetName = [IO.Path]::GetFileName([string]$release.fileName)
    if ([IO.Path]::GetExtension($manifestAssetName) -ine '.exe' -or [string]$release.sizeBytes -notmatch '^\d+$') { throw 'Сервер не указал проверенные параметры установщика.' }
    $assetSize = [long]$release.sizeBytes
    if ($assetSize -lt 1 -or $assetSize -gt 500000000) { throw 'Размер установщика в манифесте недопустим.' }
    $packageId = [Guid]::Empty
    if ([string]$release.sha256 -notmatch '^([0-9a-fA-F]{64})$' -or ![Guid]::TryParse([string]$release.id, [ref]$packageId)) { throw 'Сервер не указал SHA-256 или идентификатор установщика.' }
    $expectedHash = $Matches[1].ToUpperInvariant()
    $downloadUrl = New-Object Uri($serverBase, ('api/v1/packages/' + $packageId.ToString('D') + '/download'))

    $installer = Join-Path $updateRoot $assetName
    $cached=(Test-Path -LiteralPath $installer -PathType Leaf) -and
        (Get-Item -LiteralPath $installer).Length -eq $assetSize -and
        (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash -ceq $expectedHash
    if(!$cached) {
        New-Item -ItemType Directory -Path $updateRoot -Force | Out-Null
        Set-UpdateStatus "Найдена версия $latestVersion. Подготавливаю установщик."
        $request = [Net.HttpWebRequest]::Create($downloadUrl)
        $request.Method = 'GET'
        $request.UserAgent = 'ITSeti-Maintenance-Updater/2.4.9'
        $request.Headers.Add('X-Device-Id', [string]$device.deviceId)
        $request.Headers.Add('X-Device-Key', [string]$device.deviceKey)
        $request.Timeout = 30000
        $request.ReadWriteTimeout = 30000
        $response = $request.GetResponse()
        $inputStream = $response.GetResponseStream()
        $outputStream = [IO.File]::Open($partial, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try {
            $totalBytes = [long]$response.ContentLength
            $receivedBytes = [long]0
            $lastPercent = -1
            $buffer = [byte[]]::new(1048576)
            while (($read = $inputStream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                $outputStream.Write($buffer, 0, $read)
                $receivedBytes += $read
                if ($totalBytes -gt 0) {
                    $percent = [int][Math]::Floor(($receivedBytes * 100.0) / $totalBytes)
                    if ($percent -ge $lastPercent + 2 -or $percent -eq 100) {
                        $lastPercent = $percent
                        $receivedMb = [Math]::Floor($receivedBytes / 1MB)
                        $totalMb = [Math]::Ceiling($totalBytes / 1MB)
                        Set-UpdateStatus "Скачивание установщика: $percent% ($receivedMb из $totalMb МБ)."
                    }
                }
            }
        } finally {
            if ($outputStream) { $outputStream.Dispose() }
            if ($inputStream) { $inputStream.Dispose() }
            if ($response) { $response.Dispose() }
        }
        if ((Get-Item -LiteralPath $partial).Length -ne $assetSize) { throw 'Размер скачанного установщика не совпал с манифестом.' }
        Set-UpdateStatus 'Проверяю SHA-256 установщика.'
        $actualHash = (Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash
        if ($actualHash -cne $expectedHash) { throw 'SHA-256 установщика не совпал с данными сервера.' }
        Move-Item -LiteralPath $partial -Destination $installer -Force
    }

    Set-UpdateStatus 'Пакет загружен и проверен. Готов к установке.'
    $running=$false
    foreach ($process in Get-Process -Name 'ITSeti.Maintenance' -ErrorAction SilentlyContinue) {
        try {
            if ([string]::Equals($process.Path, $appPath, [StringComparison]::OrdinalIgnoreCase)) { $running = $true; break }
        } catch { $running = $true; break }
    }
    if($running) {
        Set-UpdateStatus 'Пакет загружен и проверен. Ожидает закрытия приложения.'
        exit 0
    }

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
