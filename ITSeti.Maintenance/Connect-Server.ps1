param([switch]$VerifyOnly)
$ErrorActionPreference = 'Stop'
$serverUrl = 'https://it-seti.nylenz.ru'
$deviceFile = Join-Path $env:ProgramData 'ITSeti\Maintenance\server-device.json'
$supportFile = Join-Path $env:ProgramData 'ITSeti\Maintenance\server-support.json'

Add-Type -AssemblyName System.Web.Extensions
$script:serializer = New-Object System.Web.Script.Serialization.JavaScriptSerializer
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

function Invoke-ServerRequest([string]$path, [string]$password, [object]$body) {
    $request = [Net.HttpWebRequest][Net.WebRequest]::Create($serverUrl + $path)
    $request.Method = 'POST'
    $request.AllowAutoRedirect = $false
    $request.Timeout = 15000
    $request.ReadWriteTimeout = 15000
    $request.ContentType = 'application/json; charset=utf-8'
    $request.Headers.Add('X-Registration-Password', $password)
    $json = if ($null -eq $body) { '{}' } else { $script:serializer.Serialize($body) }
    $bytes = [Text.Encoding]::UTF8.GetBytes($json)
    $request.ContentLength = $bytes.Length
    $stream = $request.GetRequestStream()
    try { $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
    try { $response = [Net.HttpWebResponse]$request.GetResponse() }
    catch [Net.WebException] { throw (New-Object InvalidOperationException((Format-ServerError $_.Exception))) }
    try {
        if ($response.StatusCode -ne [Net.HttpStatusCode]::OK) { throw 'Неожиданный ответ сервера.' }
        $reader = New-Object IO.StreamReader($response.GetResponseStream(), [Text.Encoding]::UTF8)
        try { return $script:serializer.DeserializeObject($reader.ReadToEnd()) }
        finally { $reader.Dispose() }
    }
    finally { $response.Dispose() }
}

function Invoke-DeviceRequest([string]$path, [string]$method, [object]$body) {
    $device = Get-Content -LiteralPath $deviceFile -Raw -Encoding UTF8 | ConvertFrom-Json
    $request = [Net.HttpWebRequest][Net.WebRequest]::Create($serverUrl + $path)
    $request.Method = $method
    $request.AllowAutoRedirect = $false
    $request.Timeout = 15000
    $request.ContentType = 'application/json; charset=utf-8'
    $request.Headers.Add('X-Device-Id', [string]$device.deviceId)
    $request.Headers.Add('X-Device-Key', [string]$device.deviceKey)
    $bytes = [Text.Encoding]::UTF8.GetBytes($script:serializer.Serialize($body))
    $request.ContentLength = $bytes.Length
    $stream = $request.GetRequestStream()
    try { $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
    try { $response = [Net.HttpWebResponse]$request.GetResponse() }
    catch [Net.WebException] { throw (New-Object InvalidOperationException((Format-ServerError $_.Exception))) }
    try {
        if ($response.StatusCode -ne [Net.HttpStatusCode]::OK) { throw 'Неожиданный ответ сервера.' }
        $reader = New-Object IO.StreamReader($response.GetResponseStream(), [Text.Encoding]::UTF8)
        try { return $script:serializer.DeserializeObject($reader.ReadToEnd()) }
        finally { $reader.Dispose() }
    } finally { $response.Dispose() }
}

function Format-ServerError([Net.WebException]$exception) {
    if (!$exception.Response) { return 'Сервер недоступен: ' + $exception.Status }
    $response = [Net.HttpWebResponse]$exception.Response
    $detail = $null
    try {
        $reader = New-Object IO.StreamReader($response.GetResponseStream(), [Text.Encoding]::UTF8)
        try {
            $body = $reader.ReadToEnd()
            if ($body) {
                $parsed = $script:serializer.DeserializeObject($body)
                if ($parsed -is [Collections.IDictionary] -and $parsed.Contains('error')) { $detail = [string]$parsed['error'] }
            }
        } finally { $reader.Dispose() }
    } catch { }
    if ($detail) { return ('HTTP {0}: {1}' -f [int]$response.StatusCode, $detail) }
    return ('Сервер вернул HTTP {0} ({1}).' -f [int]$response.StatusCode, $response.StatusDescription)
}

function Save-SupportKey([object]$result) {
    if (!$result['supportKey']) { throw 'Сервер не выдал ключ заявок.' }
    $json = $script:serializer.Serialize(@{ serverUrl=$serverUrl; deviceId=[string]$result['deviceId']; supportKey=[string]$result['supportKey'] })
    [IO.File]::WriteAllText($supportFile, $json, (New-Object Text.UTF8Encoding($false)))
    try {
        $acl = New-Object Security.AccessControl.FileSecurity
        $acl.SetAccessRuleProtection($true, $false)
        foreach ($sidText in @('S-1-5-18','S-1-5-32-544','S-1-5-32-545')) {
            $sid = New-Object Security.Principal.SecurityIdentifier($sidText)
            $rights = if ($sidText -eq 'S-1-5-32-545') { [Security.AccessControl.FileSystemRights]::Read } else { [Security.AccessControl.FileSystemRights]::FullControl }
            $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule -ArgumentList @($sid,$rights,[Security.AccessControl.AccessControlType]::Allow)))
        }
        [IO.File]::SetAccessControl($supportFile, $acl)
    } catch { [IO.File]::Delete($supportFile); throw }
}

function Save-DeviceKey([object]$result, [long]$companyId, $siteId) {
    $folder = Split-Path $deviceFile -Parent
    if (!(Test-Path -LiteralPath $folder -PathType Container)) {
        [void][IO.Directory]::CreateDirectory($folder)
    }
    $record = @{
        serverUrl = $serverUrl
        deviceId = [string]$result['deviceId']
        deviceKey = [string]$result['deviceKey']
        companyId = $companyId
        siteId = $siteId
    }
    $json = $script:serializer.Serialize($record)
    [IO.File]::WriteAllText($deviceFile, $json, (New-Object Text.UTF8Encoding($false)))
    try {
        $acl = New-Object Security.AccessControl.FileSecurity
        $acl.SetAccessRuleProtection($true, $false)
        foreach ($sidText in @('S-1-5-18', 'S-1-5-32-544')) {
            $sid = New-Object Security.Principal.SecurityIdentifier($sidText)
            $rule = New-Object Security.AccessControl.FileSystemAccessRule -ArgumentList @(
                $sid, [Security.AccessControl.FileSystemRights]::FullControl,
                [Security.AccessControl.AccessControlType]::Allow)
            $acl.AddAccessRule($rule)
        }
        [IO.File]::SetAccessControl($deviceFile, $acl)
    }
    catch {
        [IO.File]::Delete($deviceFile)
        throw
    }
}

if ($VerifyOnly) {
    $password = [Environment]::GetEnvironmentVariable('ITSETI_REGISTRATION_PASSWORD', 'Process')
    if ([string]::IsNullOrEmpty($password)) { throw 'Test password is missing.' }
    try {
        $catalog = Invoke-ServerRequest '/api/v1/catalog' $password $null
        Write-Output ('Companies: {0}; sites: {1}' -f @($catalog['companies']).Count, @($catalog['sites']).Count)
    }
    finally { [Environment]::SetEnvironmentVariable('ITSETI_REGISTRATION_PASSWORD', $null, 'Process') }
    exit
}

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[Windows.Forms.Application]::EnableVisualStyles()

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $arguments = '-STA -NoProfile -ExecutionPolicy Bypass -File "' + $MyInvocation.MyCommand.Path + '"'
    try { Start-Process -FilePath (Join-Path $PSHOME 'powershell.exe') -ArgumentList $arguments -Verb RunAs -WindowStyle Hidden }
    catch { [void][Windows.Forms.MessageBox]::Show('Для подключения нужны права администратора.', 'ИТ-Сети') }
    exit
}

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class AssignmentConsole {
    [DllImport("kernel32.dll")] public static extern IntPtr GetConsoleWindow();
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr handle, int command);
}
'@
[void][AssignmentConsole]::ShowWindow([AssignmentConsole]::GetConsoleWindow(), 0)
$script:existing = Test-Path -LiteralPath $deviceFile -PathType Leaf
$script:currentDevice = if ($script:existing) { Get-Content -LiteralPath $deviceFile -Raw -Encoding UTF8 | ConvertFrom-Json } else { $null }

$form = New-Object Windows.Forms.Form
$form.Text = if ($script:existing) { 'Компания и объект ПК' } else { 'Подключение к серверу ИТ-Сети' }
$form.ClientSize = New-Object Drawing.Size(640, 340)
$form.BackColor = [Drawing.Color]::White
$form.Font = New-Object Drawing.Font('Segoe UI', 10)
$form.StartPosition = 'CenterScreen'
$form.FormBorderStyle = 'FixedDialog'
$form.MaximizeBox = $false
$form.MinimizeBox = $false
$form.ShowInTaskbar = $true

function Add-Label([string]$text, [int]$left, [int]$top, [int]$width) {
    $label = New-Object Windows.Forms.Label
    $label.Text = $text
    $label.Location = New-Object Drawing.Point($left, $top)
    $label.Size = New-Object Drawing.Size($width, 22)
    $form.Controls.Add($label)
}

Add-Label 'Пароль подключения' 20 18 600
$passwordBox = New-Object Windows.Forms.TextBox
$passwordBox.Location = New-Object Drawing.Point(20, 44)
$passwordBox.Size = New-Object Drawing.Size(440, 28)
$passwordBox.BorderStyle = 'FixedSingle'
$passwordBox.UseSystemPasswordChar = $true
$form.Controls.Add($passwordBox)

$loadButton = New-Object Windows.Forms.Button
$loadButton.Text = 'Получить список'
$loadButton.Location = New-Object Drawing.Point(472, 42)
$loadButton.Size = New-Object Drawing.Size(148, 30)
$form.Controls.Add($loadButton)

Add-Label 'Компания' 20 91 600
$companySearch = New-Object Windows.Forms.TextBox
$companySearch.Location = New-Object Drawing.Point(20, 116)
$companySearch.Size = New-Object Drawing.Size(180, 28)
$companySearch.BorderStyle = 'FixedSingle'
$companySearch.Enabled = $false
$companySearch.AccessibleName = 'Поиск по названию компании'
$form.Controls.Add($companySearch)
$companyBox = New-Object Windows.Forms.ComboBox
$companyBox.DropDownStyle = 'DropDownList'
$companyBox.Location = New-Object Drawing.Point(210, 116)
$companyBox.Size = New-Object Drawing.Size(410, 28)
$companyBox.Enabled = $false
$form.Controls.Add($companyBox)

Add-Label 'Объект обслуживания' 20 160 600
$siteSearch = New-Object Windows.Forms.TextBox
$siteSearch.Location = New-Object Drawing.Point(20, 185)
$siteSearch.Size = New-Object Drawing.Size(180, 28)
$siteSearch.BorderStyle = 'FixedSingle'
$siteSearch.Enabled = $false
$siteSearch.AccessibleName = 'Поиск по объекту или адресу'
$form.Controls.Add($siteSearch)
$searchTips = New-Object Windows.Forms.ToolTip
$searchTips.SetToolTip($companySearch, 'Поиск по названию компании')
$searchTips.SetToolTip($siteSearch, 'Поиск по названию объекта или адресу')
$siteBox = New-Object Windows.Forms.ComboBox
$siteBox.DropDownStyle = 'DropDownList'
$siteBox.Location = New-Object Drawing.Point(210, 185)
$siteBox.Size = New-Object Drawing.Size(410, 28)
$siteBox.Enabled = $false
$form.Controls.Add($siteBox)

$status = New-Object Windows.Forms.Label
$status.Location = New-Object Drawing.Point(20, 232)
$status.Size = New-Object Drawing.Size(600, 55)
$status.ForeColor = [Drawing.Color]::FromArgb(53, 74, 94)
$status.AutoEllipsis = $true
$status.Text = 'Если сервер недоступен, завершите установку и подключитесь позже.'
$form.Controls.Add($status)

$laterButton = New-Object Windows.Forms.Button
$laterButton.Text = 'Позже'
$laterButton.Location = New-Object Drawing.Point(402, 298)
$laterButton.Size = New-Object Drawing.Size(92, 30)
$laterButton.Add_Click({ $form.Close() })
$form.Controls.Add($laterButton)

$connectButton = New-Object Windows.Forms.Button
$connectButton.Text = if ($script:existing) { 'Сохранить' } else { 'Подключить' }
$connectButton.Location = New-Object Drawing.Point(504, 298)
$connectButton.Size = New-Object Drawing.Size(116, 30)
$connectButton.Enabled = $false
$form.Controls.Add($connectButton)

$script:companies = @()
$script:sites = @()
$script:visibleCompanies = @()
$script:visibleSites = @()
$script:catalogLoaded = $false

function Update-CompanyList([string]$query) {
    $selectedId = $null
    if ($companyBox.SelectedIndex -ge 0 -and $companyBox.SelectedIndex -lt $script:visibleCompanies.Count) {
        $selectedId = [long]$script:visibleCompanies[$companyBox.SelectedIndex]['id']
    }
    $companyBox.BeginUpdate()
    try {
        $companyBox.Items.Clear()
        $script:visibleCompanies = @($script:companies | Where-Object {
            ([string]$_['name']).IndexOf($query, [StringComparison]::CurrentCultureIgnoreCase) -ge 0
        })
        foreach ($company in $script:visibleCompanies) { [void]$companyBox.Items.Add([string]$company['name']) }
    } finally { $companyBox.EndUpdate() }
    $companyBox.SelectedIndex = -1
    if ($null -ne $selectedId) {
        for ($i=0; $i -lt $script:visibleCompanies.Count; $i++) {
            if ([long]$script:visibleCompanies[$i]['id'] -eq $selectedId) { $companyBox.SelectedIndex=$i; break }
        }
    }
    if ($companyBox.SelectedIndex -lt 0 -and $script:visibleCompanies.Count -eq 1) { $companyBox.SelectedIndex = 0 }
    $connectButton.Enabled = $script:catalogLoaded -and $companyBox.SelectedIndex -ge 0 -and $siteBox.SelectedIndex -ge 0
}

function Update-SiteList([string]$query) {
    $selectedId = $null
    if ($siteBox.SelectedIndex -gt 0 -and $siteBox.SelectedIndex -lt $script:visibleSites.Count) {
        $selectedId = [long]$script:visibleSites[$siteBox.SelectedIndex]['id']
    }
    $siteBox.BeginUpdate()
    try {
        $siteBox.Items.Clear()
        $script:visibleSites = @($null)
        [void]$siteBox.Items.Add('Без объекта')
        if ($companyBox.SelectedIndex -ge 0) {
            $companyId = [long]$script:visibleCompanies[$companyBox.SelectedIndex]['id']
            foreach ($site in $script:sites) {
                if ([long]$site['companyId'] -ne $companyId) { continue }
                $caption = [string]$site['name']
                if (![string]::IsNullOrWhiteSpace([string]$site['address'])) { $caption += ' · ' + [string]$site['address'] }
                if ($caption.IndexOf($query, [StringComparison]::CurrentCultureIgnoreCase) -lt 0) { continue }
                $script:visibleSites += $site
                [void]$siteBox.Items.Add($caption)
            }
        }
    } finally { $siteBox.EndUpdate() }
    $siteBox.SelectedIndex = 0
    if ($null -ne $selectedId) {
        for ($i=1; $i -lt $script:visibleSites.Count; $i++) {
            if ([long]$script:visibleSites[$i]['id'] -eq $selectedId) { $siteBox.SelectedIndex=$i; break }
        }
    } elseif ($script:existing -and $script:currentDevice.siteId) {
        for ($i=1; $i -lt $script:visibleSites.Count; $i++) {
            if ([long]$script:visibleSites[$i]['id'] -eq [long]$script:currentDevice.siteId) { $siteBox.SelectedIndex=$i; break }
        }
    }
    $connectButton.Enabled = $script:catalogLoaded -and $companyBox.SelectedIndex -ge 0 -and $siteBox.SelectedIndex -ge 0
}

$companyBox.Add_SelectedIndexChanged({
    Update-SiteList $siteSearch.Text
})
$companySearch.Add_TextChanged({ Update-CompanyList $companySearch.Text })
$siteSearch.Add_TextChanged({ Update-SiteList $siteSearch.Text })
$siteBox.Add_SelectedIndexChanged({
    $connectButton.Enabled = $script:catalogLoaded -and $companyBox.SelectedIndex -ge 0 -and $siteBox.SelectedIndex -ge 0
})

$loadButton.Add_Click({
    if ([string]::IsNullOrWhiteSpace($passwordBox.Text)) {
        $status.Text = 'Введите пароль подключения.'
        return
    }
    $loadButton.Enabled = $false
    $connectButton.Enabled = $false
    $status.Text = 'Получаем компании и объекты...'
    $form.Refresh()
    try {
        $catalog = Invoke-ServerRequest '/api/v1/catalog' $passwordBox.Text $null
        $script:companies = @($catalog['companies'])
        $script:sites = @($catalog['sites'])
        $script:catalogLoaded = $script:companies.Count -gt 0
        $companySearch.Enabled = $script:catalogLoaded
        $siteSearch.Enabled = $script:catalogLoaded
        $companyBox.Enabled = $script:catalogLoaded
        $siteBox.Enabled = $script:catalogLoaded
        Update-CompanyList $companySearch.Text
        if ($script:existing) {
            for ($i=0; $i -lt $script:visibleCompanies.Count; $i++) {
                if ([long]$script:visibleCompanies[$i]['id'] -eq [long]$script:currentDevice.companyId) { $companyBox.SelectedIndex=$i; break }
            }
        }
        $siteCount = $script:sites.Count
        $status.Text = 'Получено компаний: {0}; объектов: {1}. Введите часть названия или адреса для поиска.' -f $script:companies.Count,$siteCount
    }
    catch {
        $failure = $_.Exception
        while ($failure.InnerException) { $failure = $failure.InnerException }
        if ($failure -is [Net.WebException] -and $failure.Response -and
            $failure.Response.StatusCode -eq [Net.HttpStatusCode]::Unauthorized) {
            $status.Text = 'Неверный пароль подключения.'
        }
        else { $status.Text = $_.Exception.Message }
    }
    finally { $loadButton.Enabled = $true }
})

$connectButton.Add_Click({
    if ($companyBox.SelectedIndex -lt 0 -or $siteBox.SelectedIndex -lt 0) {
        $status.Text = 'Выберите компанию.'
        return
    }
    $connectButton.Enabled = $false
    $status.Text = 'Регистрируем компьютер...'
    $form.Refresh()
    try {
        $companyId = [long]$script:visibleCompanies[$companyBox.SelectedIndex]['id']
        $siteId = if ($siteBox.SelectedIndex -eq 0) { $null } else { [long]$script:visibleSites[$siteBox.SelectedIndex]['id'] }
        $inventoryFile = Join-Path $env:ProgramData 'ITSeti\Maintenance\inventory.txt'
        $inventory = if (Test-Path -LiteralPath $inventoryFile -PathType Leaf) {
            [IO.File]::ReadAllText($inventoryFile).Trim()
        } else { $null }
        $serialNumber = $null
        $hardwareUuid = $null
        try {
            $bios = Get-WmiObject -Class Win32_BIOS -ErrorAction Stop | Select-Object -First 1
            $serialNumber = [string]$bios.SerialNumber
        } catch { }
        try {
            $product = Get-WmiObject -Class Win32_ComputerSystemProduct -ErrorAction Stop | Select-Object -First 1
            $hardwareUuid = [string]$product.UUID
        } catch { }
        $body = @{
            companyId = $companyId
            siteId = $siteId
            computerName = $env:COMPUTERNAME
            serialNumber = $serialNumber
            hardwareUuid = $hardwareUuid
            inventoryNumber = $inventory
        }
        if ($script:existing) {
            try {
                [void](Invoke-DeviceRequest '/api/v1/assignment' 'PUT' $body)
                $script:currentDevice.companyId = $companyId
                $script:currentDevice.siteId = $siteId
                $updated = @{ serverUrl=$serverUrl; deviceId=[string]$script:currentDevice.deviceId
                    deviceKey=[string]$script:currentDevice.deviceKey; companyId=$companyId; siteId=$siteId }
                [IO.File]::WriteAllText($deviceFile,$script:serializer.Serialize($updated),(New-Object Text.UTF8Encoding($false)))
                if (!(Test-Path -LiteralPath $supportFile -PathType Leaf)) {
                    Save-SupportKey (Invoke-DeviceRequest '/api/v1/support-key' 'POST' @{})
                }
            }
            catch {
                if ($_.Exception.Message -notmatch 'HTTP (401|404)') { throw }
                $status.Text = 'Восстанавливаем регистрацию этого ПК по серийному номеру...'
                $form.Refresh()
                $result = Invoke-ServerRequest '/api/v1/enroll' $passwordBox.Text $body
                Save-DeviceKey $result $companyId $siteId
                Save-SupportKey $result
            }
        } else {
            $result = Invoke-ServerRequest '/api/v1/enroll' $passwordBox.Text $body
            Save-DeviceKey $result $companyId $siteId
            Save-SupportKey $result
        }
        $passwordBox.Clear()
        $form.Close()
    }
    catch {
        $status.Text = 'Подключение не завершено: ' + $_.Exception.Message
        $connectButton.Enabled = $true
    }
})

$form.Add_Shown({ $form.Activate(); $passwordBox.Focus() })
[void]$form.ShowDialog()
$passwordBox.Clear()
$form.Dispose()
