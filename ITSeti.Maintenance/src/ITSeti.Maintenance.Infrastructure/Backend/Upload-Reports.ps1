param([switch]$VerifyOnly)
$ErrorActionPreference = 'Stop'
$root = Join-Path $env:ProgramData 'ITSeti\Maintenance'
$deviceFile = Join-Path $root 'server-device.json'
$queue = Join-Path $root 'ReportQueue'
$runs = Join-Path $root 'Runs'
if (!(Test-Path -LiteralPath $deviceFile -PathType Leaf)) { exit 0 }
$device = Get-Content -LiteralPath $deviceFile -Raw -Encoding UTF8 | ConvertFrom-Json
if (!$device.deviceId -or !$device.deviceKey -or $device.serverUrl -ne 'https://it-seti.nylenz.ru') { exit 1 }
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

function Get-Identity {
    $inventory = $null
    $rms = $null
    $anyDesk = $null
    $admins = @()
    try {
        $value = [IO.File]::ReadAllText((Join-Path $root 'inventory.txt')).Trim()
        if ($value -match '^\d{4}$') { $inventory = $value }
    } catch {}
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
    return @{ InventoryNumber = $inventory; RmsId = $rms; AnyDeskId = $anyDesk; AdminAccounts = $admins }
}

function Send-Report([string]$path, [hashtable]$identity) {
    $report = Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($name in $identity.Keys) {
        $report | Add-Member -NotePropertyName $name -NotePropertyValue $identity[$name] -Force
    }
    $json = $report | ConvertTo-Json -Depth 30 -Compress
    $bytes = [Text.Encoding]::UTF8.GetBytes($json)
    if ($bytes.Length -gt 12MB) { throw 'Отчёт превышает лимит сервера 12 МБ.' }
    $request = [Net.HttpWebRequest][Net.WebRequest]::Create($device.serverUrl + '/api/v1/check-runs')
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

if ($VerifyOnly) { Get-Identity | ConvertTo-Json -Compress; exit 0 }
try {
    $policyPath = Join-Path $root 'process-policy.json'
    if (!(Test-Path -LiteralPath $policyPath) -or (Get-Item -LiteralPath $policyPath).LastWriteTimeUtc -lt [DateTime]::UtcNow.AddDays(-1)) {
        $request = [Net.HttpWebRequest][Net.WebRequest]::Create($device.serverUrl + '/api/v1/process-policy')
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
$identity = Get-Identity
$pending = @()
if (Test-Path -LiteralPath $queue -PathType Container) {
    $pending += @(Get-ChildItem -LiteralPath $queue -File -Filter '*.json' | Sort-Object LastWriteTimeUtc)
}
if (Test-Path -LiteralPath $runs -PathType Container) {
    $pending += @(Get-ChildItem -LiteralPath $runs -File -Filter 'result.json' -Recurse |
        Where-Object { !(Test-Path -LiteralPath (Join-Path $_.DirectoryName 'server-uploaded.txt')) } |
        Sort-Object LastWriteTimeUtc)
}
foreach ($file in $pending | Select-Object -First 200) {
    try {
        Send-Report $file.FullName $identity
        if ($file.DirectoryName -eq $queue) { Remove-Item -LiteralPath $file.FullName -Force }
        else { [IO.File]::WriteAllText((Join-Path $file.DirectoryName 'server-uploaded.txt'),[DateTimeOffset]::UtcNow.ToString('O'),[Text.Encoding]::ASCII) }
    } catch {
        $message = $_.Exception.GetType().Name + ': ' + $_.Exception.Message
        [IO.File]::WriteAllText((Join-Path $root 'server-upload-error.txt'),$message,[Text.Encoding]::UTF8)
        exit 1
    }
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
