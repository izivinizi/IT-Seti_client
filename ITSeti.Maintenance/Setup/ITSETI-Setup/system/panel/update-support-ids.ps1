# update-support-ids.ps1 - collects this PC remote-support IDs and a clean OS name into the registry
# for the Desktop Info panel. No admin rights required (writes to HKCU).

function Get-RmsId {
    $keys = @(
        'HKLM:\SOFTWARE\TektonIT\RMS Host\Host\Parameters',
        'HKLM:\SOFTWARE\WOW6432Node\TektonIT\RMS Host\Host\Parameters'
    )
    foreach ($k in $keys) {
        try { $blob = (Get-ItemProperty -Path $k -Name InternetId -ErrorAction Stop).InternetId }
        catch { continue }
        if (-not $blob) { continue }
        $xml = [System.Text.Encoding]::UTF8.GetString($blob)
        if ($xml -match '<internet_id>([^<]+)</internet_id>') { return $Matches[1].Trim() }
    }
    return ''
}

function Get-AnyDeskId {
    # system.conf only. service.conf next to it contains the client private key.
    foreach ($root in @($env:ProgramData, $env:APPDATA)) {
        if (-not $root) { continue }
        $dirs = Get-ChildItem -Path $root -Filter 'AnyDesk*' -Directory -ErrorAction SilentlyContinue
        foreach ($d in $dirs) {
            $f = Join-Path $d.FullName 'system.conf'
            if (-not (Test-Path $f)) { continue }
            $m = Select-String -Path $f -Pattern 'ad\.anynet\.id=(\d+)' -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($m) { return $m.Matches[0].Groups[1].Value }
        }
    }
    return ''
}

$key = 'HKCU:\SOFTWARE\ITSETI\SupportIds'
if (-not (Test-Path $key)) { New-Item -Path $key -Force | Out-Null }

function Get-OsName {
    # "Майкрософт Windows 11 Pro" / "Microsoft Windows 11 Pro" -> "Windows 11 Pro"
    try { $cap = (Get-CimInstance Win32_OperatingSystem -ErrorAction Stop).Caption }
    catch { try { $cap = (Get-WmiObject Win32_OperatingSystem -ErrorAction Stop).Caption } catch { return '' } }
    return ($cap -replace '^\s*(Майкрософт|Microsoft)\s+', '').Trim()
}

$pairs = @{ RmsId = Get-RmsId; AnyDeskId = Get-AnyDeskId; OsName = Get-OsName }
foreach ($name in $pairs.Keys) {
    $value = $pairs[$name]
    if ($value) { Set-ItemProperty -Path $key -Name $name -Value $value }
    else { Remove-ItemProperty -Path $key -Name $name -ErrorAction SilentlyContinue }
}