param([switch]$VerifyOnly,[switch]$RefreshPolicyOnly)
$ErrorActionPreference = 'Stop'
$root = Join-Path $env:ProgramData 'ITSeti\Maintenance'
$deviceFile = Join-Path $root 'server-device.json'
$queue = Join-Path $root 'ReportQueue'
$monitoringQueue = Join-Path $root 'MonitoringQueue'
$runs = Join-Path $root 'Runs'
if (!(Test-Path -LiteralPath $deviceFile -PathType Leaf)) { exit 0 }
$device = Get-Content -LiteralPath $deviceFile -Raw -Encoding UTF8 | ConvertFrom-Json
try {
    $configuredServer = New-Object System.Uri([string]$device.serverUrl)
    if (!$configuredServer.IsAbsoluteUri -or $configuredServer.Scheme -ne 'https' -or
        $configuredServer.Host -ne 'it-seti.nylenz.ru' -or $configuredServer.Port -notin @(-1,443)) { exit 1 }
    $serverBase = New-Object System.Uri('https://it-seti.nylenz.ru/')
} catch { exit 1 }
if (!$device.deviceId -or !$device.deviceKey) { exit 1 }
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

function New-ServerUri([string]$route) {
    return New-Object System.Uri($serverBase, $route.TrimStart('/'))
}

function Get-Identity {
    $inventory = $null
    $rms = $null
    $anyDesk = $null
    $ocsPresent = $false
    $admins = @()
    try {
        $value = [IO.File]::ReadAllText((Join-Path $root 'inventory.txt')).Trim()
        if ($value -match '^\d{4}$') { $inventory = $value }
    } catch {}
    try {
        $ocsPresent = [bool](Get-Service -Name 'OCS Inventory Service' -ErrorAction SilentlyContinue)
        if (!$ocsPresent) { $ocsPresent = Test-Path -LiteralPath (Join-Path ${env:ProgramFiles} 'OCS Inventory Agent\OcsService.exe') }
        if (!$ocsPresent -and ${env:ProgramFiles(x86)}) { $ocsPresent = Test-Path -LiteralPath (Join-Path ${env:ProgramFiles(x86)} 'OCS Inventory Agent\OcsService.exe') }
    } catch {}
    try {
        $rmsServiceInstalled = Test-Path -LiteralPath 'Registry::HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\RManService'
        $rmsExecutable = @(${env:ProgramFiles},${env:ProgramFiles(x86)} | Where-Object { $_ } |
            ForEach-Object { Join-Path $_ 'Remote Manipulator System - Host\rutserv.exe' } |
            Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1)
        if ($rmsServiceInstalled -or $rmsExecutable.Count -gt 0) {
            foreach ($path in @('SOFTWARE\TektonIT\RMS Host\Host\Parameters','SOFTWARE\WOW6432Node\TektonIT\RMS Host\Host\Parameters')) {
                try {
                    $value = (Get-ItemProperty -LiteralPath ('Registry::HKEY_LOCAL_MACHINE\' + $path) -ErrorAction Stop).InternetId
                    if ($value -is [byte[]]) { $value = [Text.Encoding]::UTF8.GetString($value).Trim([char]0) }
                    if ($value) {
                        [xml]$xml = ([string]$value).TrimStart([char]0xFEFF)
                        $id = [string]$xml.SelectSingleNode('//*[local-name()="internet_id"]').InnerText
                        if ($id) { $rms = $id; break }
                    }
                } catch {}
            }
        }
    } catch {}
    try {
        foreach ($directory in @(Get-ChildItem -LiteralPath $env:ProgramData -Directory -Filter 'AnyDesk*' -ErrorAction Stop)) {
            $config = Join-Path $directory.FullName 'system.conf'
            if (!(Test-Path -LiteralPath $config -PathType Leaf)) { continue }
            $match = [regex]::Match([IO.File]::ReadAllText($config), '(?m)^\s*ad\.anynet\.id=(\d{6,20})\s*$')
            if ($match.Success) { $anyDesk = $match.Groups[1].Value; break }
        }
    } catch {}
    try {
        $group = Get-WmiObject Win32_Group -Filter "SID='S-1-5-32-544'" -ErrorAction Stop
        if ($group) {
            $admins = @($group.GetRelated('Win32_Account') | ForEach-Object { $_.Domain + '\' + $_.Name } |
                Where-Object { $_ } | Sort-Object -Unique)
        }
    } catch {}
    return @{ InventoryNumber = $inventory; RmsId = $rms; AnyDeskId = $anyDesk; OcsPresent = $ocsPresent; AdminAccounts = $admins }
}

function Send-Report([string]$path, [hashtable]$identity) {
    $report = Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($name in $identity.Keys) {
        $report | Add-Member -NotePropertyName $name -NotePropertyValue $identity[$name] -Force
    }
    if(!$report.ApplicationErrors -and [string]$report.KindLabel -ne 'Постоянный мониторинг'){
        $errorLog=Join-Path $root 'Logs\application-errors.jsonl'
        $recentErrors=@()
        if(Test-Path -LiteralPath $errorLog){
            foreach($line in @(Get-Content -LiteralPath $errorLog -Encoding UTF8 -Tail 100)){
                try {
                    $entry=$line|ConvertFrom-Json
                    $occurred=[DateTimeOffset]::MinValue
                    if([DateTimeOffset]::TryParse([string]$entry.occurredAt,[ref]$occurred) -and $occurred -gt [DateTimeOffset]::UtcNow.AddDays(-1)){
                        $recentErrors+=@{Id=$entry.id;OccurredAt=$entry.occurredAt;Component=$entry.component;Operation=$entry.operation;Summary=$entry.summary;ExceptionType=$entry.exceptionType}
                    }
                }catch{}
            }
        }
        $report|Add-Member -NotePropertyName ApplicationErrors -NotePropertyValue @($recentErrors|Select-Object -Last 20) -Force
        $report|Add-Member -NotePropertyName HasApplicationErrors -NotePropertyValue ($recentErrors.Count -gt 0) -Force
    }
    $json = $report | ConvertTo-Json -Depth 30 -Compress
    $bytes = [Text.Encoding]::UTF8.GetBytes($json)
    if ($bytes.Length -gt 12MB) { throw 'Отчёт превышает лимит сервера 12 МБ.' }
    $request = [Net.HttpWebRequest][Net.WebRequest]::Create((New-ServerUri '/api/v1/check-runs'))
    $request.Method = 'POST'
    $request.AllowAutoRedirect = $false
    $request.Timeout = 30000
    $request.ReadWriteTimeout = 30000
    $request.ContentType = 'application/json; charset=utf-8'
    $request.Headers.Add('X-Device-Id', [string]$device.deviceId)
    $request.Headers.Add('X-Device-Key', [string]$device.deviceKey)
    $request.ContentLength = $bytes.Length
    $stream = $request.GetRequestStream()
    try { $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
    $response = [Net.HttpWebResponse]$request.GetResponse()
    try {
        if ($response.StatusCode -ne [Net.HttpStatusCode]::OK) { throw "Сервер вернул HTTP $([int]$response.StatusCode)." }
    } finally { $response.Dispose() }
}

function Send-ApplicationErrors($device) {
    $outbox = Join-Path $root 'Logs\server-outbox'
    if (!(Test-Path -LiteralPath $outbox -PathType Container)) { return }
    for ($batch = 0; $batch -lt 5; $batch++) {
        $files = @(Get-ChildItem -LiteralPath $outbox -File -Filter '*.json' -ErrorAction Stop |
            Sort-Object LastWriteTimeUtc | Select-Object -First 100)
        if (!$files.Count) { return }
        $entries = @()
        foreach ($file in $files) {
            $entry = Get-Content -LiteralPath $file.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
            if (!$entry.id -or !$entry.occurredAt -or !$entry.component -or !$entry.operation -or !$entry.summary) {
                throw "Повреждена запись журнала приложения: $($file.Name)"
            }
            $entries += $entry
        }
        $json = @{ errors = [object[]]$entries } | ConvertTo-Json -Depth 8 -Compress
        $bytes = [Text.Encoding]::UTF8.GetBytes($json)
        if ($bytes.Length -gt 2MB) { throw 'Пакет ошибок приложения превышает 2 МБ.' }
        $request = [Net.HttpWebRequest][Net.WebRequest]::Create((New-ServerUri '/api/v1/application-errors'))
        $request.Method = 'POST'
        $request.AllowAutoRedirect = $false
        $request.Timeout = 30000
        $request.ReadWriteTimeout = 30000
        $request.ContentType = 'application/json; charset=utf-8'
        $request.Headers.Add('X-Device-Id', [string]$device.deviceId)
        $request.Headers.Add('X-Device-Key', [string]$device.deviceKey)
        $request.ContentLength = $bytes.Length
        $stream = $request.GetRequestStream()
        try { $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
        $response = [Net.HttpWebResponse]$request.GetResponse()
        try {
            if ($response.StatusCode -ne [Net.HttpStatusCode]::OK) { throw "Сервер не принял журнал ошибок (HTTP $([int]$response.StatusCode))." }
        } finally { $response.Dispose() }
        foreach ($file in $files) { Remove-Item -LiteralPath $file.FullName -Force }
    }
}

if ($VerifyOnly) { Get-Identity | ConvertTo-Json -Compress; exit 0 }
try {
    $policyPath = Join-Path $root 'process-policy.json'
    if ($RefreshPolicyOnly -or !(Test-Path -LiteralPath $policyPath) -or (Get-Item -LiteralPath $policyPath).LastWriteTimeUtc -lt [DateTime]::UtcNow.AddDays(-1)) {
        $request = [Net.HttpWebRequest][Net.WebRequest]::Create((New-ServerUri '/api/v1/process-policy'))
        $request.AllowAutoRedirect = $false
        $request.Timeout = 5000
        $request.ReadWriteTimeout = 5000
        $request.Headers.Add('X-Device-Id', [string]$device.deviceId)
        $request.Headers.Add('X-Device-Key', [string]$device.deviceKey)
        $response = $request.GetResponse()
        try {
            $reader = New-Object IO.StreamReader($response.GetResponseStream())
            try {
                $text = New-Object Text.StringBuilder
                $buffer = New-Object char[] 4096
                while (($count = $reader.Read($buffer,0,$buffer.Length)) -gt 0) {
                    if ($text.Length + $count -gt 2MB) { throw 'Process policy exceeds 2 MB.' }
                    [void]$text.Append($buffer,0,$count)
                }
                $json = $text.ToString()
            } finally { $reader.Dispose() }
        } finally { $response.Dispose() }
        if ($json.Length -gt 2MB) { throw 'Слишком большой список процессов.' }
        $policy = $json | ConvertFrom-Json
        if ($null -eq $policy.allowedNames -or $null -eq $policy.allowedPublishers) { throw 'Неверный список процессов.' }
        $temporary = $policyPath + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
        try {
            [IO.File]::WriteAllText($temporary,$json,[Text.Encoding]::UTF8)
            $acl = New-Object Security.AccessControl.FileSecurity
            $acl.SetAccessRuleProtection($true,$false)
            foreach ($sid in @('S-1-5-18','S-1-5-32-544')) {
                $identity = New-Object Security.Principal.SecurityIdentifier($sid)
                $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($identity,'FullControl','Allow')))
            }
            $users = New-Object Security.Principal.SecurityIdentifier('S-1-5-32-545')
            $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($users,'Read','Allow')))
            Set-Acl -LiteralPath $temporary -AclObject $acl
            if (Test-Path -LiteralPath $policyPath) { [IO.File]::Replace($temporary,$policyPath,$null) }
            else { [IO.File]::Move($temporary,$policyPath) }
        } finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force } }
    }
} catch { Write-Verbose 'Список процессов не обновлён; используется сохранённая копия.' }
if($RefreshPolicyOnly){exit 0}
$identity = Get-Identity
$failures = @()
try { Send-ApplicationErrors $device }
catch { $failures += ('Журнал ошибок приложения: ' + $_.Exception.GetType().Name + ': ' + $_.Exception.Message) }
$pending = @()
if (Test-Path -LiteralPath $queue -PathType Container) {
    $pending += @(Get-ChildItem -LiteralPath $queue -File -Filter '*.json' | Sort-Object LastWriteTimeUtc)
}
if (Test-Path -LiteralPath $monitoringQueue -PathType Container) {
    $pending += @(Get-ChildItem -LiteralPath $monitoringQueue -File -Filter '*.json' | Sort-Object LastWriteTimeUtc)
}
if (Test-Path -LiteralPath $runs -PathType Container) {
    $pending += @(Get-ChildItem -LiteralPath $runs -File -Filter 'result.json' -Recurse |
        Where-Object { !(Test-Path -LiteralPath (Join-Path $_.DirectoryName 'server-uploaded.txt')) } |
        Sort-Object LastWriteTimeUtc)
}
foreach ($file in $pending | Select-Object -First 200) {
    try {
        Send-Report $file.FullName $identity
        if ($file.DirectoryName -in @($queue, $monitoringQueue)) { Remove-Item -LiteralPath $file.FullName -Force }
        else { [IO.File]::WriteAllText((Join-Path $file.DirectoryName 'server-uploaded.txt'),[DateTimeOffset]::UtcNow.ToString('O'),[Text.Encoding]::ASCII) }
    } catch {
        $message = $_.Exception.GetType().Name + ': ' + $_.Exception.Message
        $failures += ($file.FullName + ': ' + $message)
    }
}
if ($failures.Count) {
    [IO.File]::WriteAllText((Join-Path $root 'server-upload-error.txt'),($failures -join [Environment]::NewLine),[Text.Encoding]::UTF8)
    exit 1
}
if (Test-Path -LiteralPath (Join-Path $root 'server-upload-error.txt')) {
    Remove-Item -LiteralPath (Join-Path $root 'server-upload-error.txt') -Force
}
try {
    & (Join-Path $PSScriptRoot 'ServerSoftware.ps1') -AutoUpdate
} catch {
    [IO.File]::WriteAllText((Join-Path $root 'server-software-error.txt'),$_.Exception.Message,[Text.Encoding]::UTF8)
}
exit 0
