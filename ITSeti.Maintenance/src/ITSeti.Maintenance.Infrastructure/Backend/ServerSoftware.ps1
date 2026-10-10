param([string]$Component, [switch]$AutoUpdate, [switch]$CatalogOnly)
$ErrorActionPreference = 'Stop'
$requestedComponent = $Component
function Normalize-ComponentKey([string]$Value) {
    $normalized = ([string]$Value).Trim().ToLowerInvariant()
    switch -Regex ($normalized) {
        '^anydesk([\s._-].*)?$' { return 'anydesk' }
        '^rms([\s._-].*)?$' { return 'rms' }
        '^ocs([\s._-].*)?$' { return 'ocs' }
        '^panel$|^desktop[\s._-]*info([\s._-].*)?$' { return 'panel' }
        '^winrar([\s._-].*)?$' { return 'winrar' }
        '^yandex([\s._-].*)?$' { return 'yandex' }
        default { return $normalized }
    }
}
$componentKey = if ([string]::IsNullOrWhiteSpace($Component)) { $null } else { Normalize-ComponentKey $Component }
$root = Join-Path $env:ProgramData 'ITSeti\Maintenance'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
if ($identity.User.Value -ne 'S-1-5-18') { throw 'Server software worker requires SYSTEM.' }
$deviceFile = Join-Path $root 'server-device.json'
if (!(Test-Path -LiteralPath $deviceFile)) { exit 0 }
$device = Get-Content -LiteralPath $deviceFile -Raw -Encoding UTF8 | ConvertFrom-Json
if ($device.serverUrl -ne 'https://it-seti.nylenz.ru' -or !$device.deviceId -or !$device.deviceKey) { throw 'Invalid server enrollment.' }
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$mutex = New-Object Threading.Mutex($false,'Global\ITSeti-ServerSoftware')
$locked = $false
function New-DownloadRequest([string]$Route) {
    $request = [Net.HttpWebRequest][Net.WebRequest]::Create($device.serverUrl + $Route)
    $request.AllowAutoRedirect = $false
    $request.Timeout = 30000
    $request.ReadWriteTimeout = 30000
    $request.Headers.Add('X-Device-Id',[string]$device.deviceId)
    $request.Headers.Add('X-Device-Key',[string]$device.deviceKey)
    return $request
}
function Find-InstalledFile([string]$Key) {
    $relative = switch ($Key.ToLowerInvariant()) {
        'rms' { 'Remote Manipulator System - Host\rutserv.exe' }
        'anydesk' { 'AnyDesk\AnyDesk.exe' }
        'ocs' { 'OCS Inventory Agent\OcsService.exe' }
        'panel' { 'Desktop Info\DesktopInfo.exe' }
        'winrar' { 'WinRAR\WinRAR.exe' }
        'yandex' { 'Yandex\YandexBrowser\Application\browser.exe' }
    }
    if (!$relative) { return $null }
    foreach ($base in @($env:ProgramFiles,${env:ProgramFiles(x86)}) | Where-Object { $_ }) {
        $path = Join-Path $base $relative
        if (Test-Path -LiteralPath $path -PathType Leaf) { return $path }
    }
}
function Assert-ServerPackage($Package) {
    $id = [Guid]::Parse([string]$Package.id)
    $key = ([string]$Package.key).ToLowerInvariant()
    $extension = [IO.Path]::GetExtension([string]$Package.fileName).ToLowerInvariant()
    if ($id -eq [Guid]::Empty -or $key -notin @('rms','anydesk','ocs','panel','winrar','yandex') -or
        [string]$Package.sha256 -notmatch '^[a-fA-F0-9]{64}$' -or [long]$Package.sizeBytes -le 0 -or [long]$Package.sizeBytes -gt 1GB -or
        ($key -eq 'rms' -and $extension -ne '.msi') -or ($key -ne 'rms' -and $extension -ne '.exe')) { throw 'Invalid package metadata or installer type.' }
}
try {
    try { $locked = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $locked = $true }
    if (!$locked) { throw 'Another server software operation is running.' }
    $request = New-DownloadRequest '/api/v1/updates'
    $response = $request.GetResponse()
    try {
        $reader = New-Object IO.StreamReader($response.GetResponseStream())
        try {
            $text = New-Object Text.StringBuilder
            $buffer = New-Object char[] 4096
            while (($count = $reader.Read($buffer,0,$buffer.Length)) -gt 0) {
                if ($text.Length + $count -gt 2MB) { throw 'Software catalog exceeds 2 MB.' }
                [void]$text.Append($buffer,0,$count)
            }
            $json = $text.ToString()
        } finally { $reader.Dispose() }
    } finally { $response.Dispose() }
    # Windows PowerShell 5.1 collapses a JSON array when it is piped into
    # ConvertFrom-Json, producing one object with array-valued properties.
    # InputObject preserves one catalog entry per object.
    $catalog = @((ConvertFrom-Json -InputObject $json))
    $cache = Join-Path $root 'software-catalog.json'
    $temporary = $cache + '.' + [Guid]::NewGuid().ToString('N') + '.pending'
    try {
        [IO.File]::WriteAllText($temporary,$json,[Text.Encoding]::UTF8)
        Move-Item -LiteralPath $temporary -Destination $cache -Force
    } finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force } }
    if ($CatalogOnly) { exit 0 }
    $selected = @(
        foreach ($entry in $catalog) {
            if ($entry.platform -notin @('windows','any')) { continue }
            $entryKey = Normalize-ComponentKey ([string]$entry.key)
            if ($entryKey -eq 'application') { continue }
            if ($componentKey) {
                if ($entryKey -eq $componentKey) { $entry }
            } elseif ($AutoUpdate -or $entryKey -in @('rms','anydesk','ocs','panel')) {
                $entry
            }
        }
    )
    # A platform-specific package takes precedence for a full install. A manual component
    # selection is already unique and must remain intact on Windows PowerShell 5.1.
    if (!$componentKey) {
        $selected = @($selected | Group-Object { Normalize-ComponentKey ([string]$_.key) } | ForEach-Object { $_.Group | Sort-Object @{Expression={if($_.platform -eq 'windows'){0}else{1}}} | Select-Object -First 1 })
    }
    if ($componentKey -and @($selected).Count -eq 0) { throw "Selected component '$requestedComponent' is absent from the current server catalog." }
    $failures = @()
    foreach ($package in $selected) {
        try {
            $key = ([string]$package.key).ToLowerInvariant()
            if ($key -notin @('rms','anydesk','ocs','panel','winrar','yandex')) { throw "No verified silent installation recipe for $key." }
            $unknownMarker = Join-Path $root ('server-install-'+$key+'.unknown')
            if (Test-Path -LiteralPath $unknownMarker) { throw 'Previous installer timed out. An administrator must review it before retrying.' }
            $installed = Find-InstalledFile $key
            if ($AutoUpdate) {
                if (!$installed) { continue }
                $current = $null; $required = $null
                $currentText = (Get-Item -LiteralPath $installed).VersionInfo.FileVersion -replace '[^0-9.].*$',''
                if (![version]::TryParse($currentText,[ref]$current) -or ![version]::TryParse([string]$package.version,[ref]$required)) { continue }
                if ($current -ge $required) { continue }
            }
            $id = [Guid]::Parse([string]$package.id)
            $hash = [string]$package.sha256
            $size = [long]$package.sizeBytes
            $extension = [IO.Path]::GetExtension([string]$package.fileName).ToLowerInvariant()
            Assert-ServerPackage $package
            $downloadRoot = Join-Path $root 'ServerPackages'
            New-Item -ItemType Directory -Path $downloadRoot -Force | Out-Null
            $installer = Join-Path $downloadRoot ($id.ToString('N') + $extension)
            $partial = $installer + '.pending'
            try {
                $request = New-DownloadRequest ('/api/v1/packages/' + $id.ToString('D') + '/download')
                $response = $request.GetResponse()
                try {
                    $input = $response.GetResponseStream()
                    $output = [IO.File]::Open($partial,[IO.FileMode]::Create,[IO.FileAccess]::Write,[IO.FileShare]::None)
                    try {
                        $buffer = New-Object byte[] 65536
                        $received = 0L
                        while (($count = $input.Read($buffer,0,$buffer.Length)) -gt 0) {
                            $received += $count
                            if ($received -gt $size) { throw 'Package exceeds declared size.' }
                            $output.Write($buffer,0,$count)
                        }
                        if ($received -ne $size) { throw 'Incomplete package download.' }
                    } finally { $output.Dispose(); $input.Dispose() }
                } finally { $response.Dispose() }
                if ((Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash -ne $hash) { throw 'Package SHA-256 mismatch.' }
                Move-Item -LiteralPath $partial -Destination $installer -Force
            } finally { if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial -Force } }
            $program86 = if (${env:ProgramFiles(x86)}) { ${env:ProgramFiles(x86)} } else { $env:ProgramFiles }
            $executable = $installer
            $arguments = switch ($key) {
                'rms' { $executable = Join-Path $env:WINDIR 'System32\msiexec.exe'; '/i "'+$installer+'" /qn /norestart REBOOT=ReallySuppress /l*v "'+(Join-Path $root 'server-rms-install.log')+'"' }
                'anydesk' { '--install "'+(Join-Path $program86 'AnyDesk')+'" --silent --start-with-win' }
                'ocs' { if ($installed) { '/S /NOSPLASH /UPGRADE' } else { '/S /NOSPLASH' } }
                'panel' { '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /NOCANCEL /CLOSEAPPLICATIONS /DIR="'+(Join-Path $env:ProgramFiles 'Desktop Info')+'"' }
                'winrar' { '/S' }
                'yandex' { '--silent --system-level --do-not-launch-browser' }
            }
            $process = Start-Process -FilePath $executable -ArgumentList $arguments -WorkingDirectory $downloadRoot -WindowStyle Hidden -PassThru
            $completedFromInstallMarker = $false
            try {
                if ($key -eq 'ocs' -and !$installed) {
                    # Some OCS setup builds install the service and then keep their
                    # bootstrap process alive indefinitely under SYSTEM. The service
                    # and executable are the authoritative completion markers.
                    $markerDeadline = [DateTime]::UtcNow.AddMinutes(2)
                    while (!$process.HasExited -and [DateTime]::UtcNow -lt $markerDeadline) {
                        if (Find-InstalledFile $key) {
                            $completedFromInstallMarker = $true
                            break
                        }
                        [void]$process.WaitForExit(1000)
                    }
                    if ($completedFromInstallMarker -and !$process.HasExited) {
                        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
                    }
                }
                if (!$completedFromInstallMarker -and !$process.WaitForExit(900000)) {
                    [IO.File]::WriteAllText($unknownMarker,[string]$process.Id,[Text.Encoding]::ASCII)
                    throw 'Silent installer exceeded 15 minutes; inspect its log before retrying.'
                }
                if (!$completedFromInstallMarker -and $process.ExitCode -notin @(0,3010)) { throw "Silent installer exited with code $($process.ExitCode)." }
            } finally { $process.Dispose() }
            if (!(Find-InstalledFile $key)) { throw 'Installer returned success but the installed component was not found.' }
            if ($key -eq 'panel') {
                $source = Join-Path (Split-Path -Parent $PSScriptRoot) 'Panel'
                $target = Join-Path $env:ProgramData 'ITSETI'
                New-Item -ItemType Directory -Path $target -Force | Out-Null
                foreach ($file in @('DesktopInfo.ini','update-support-ids.ps1','start-panel.vbs')) {
                    Copy-Item -LiteralPath (Join-Path $source $file) -Destination (Join-Path $target $file) -Force
                }
                Copy-Item -LiteralPath (Join-Path $target 'DesktopInfo.ini') -Destination (Join-Path $env:ProgramFiles 'Desktop Info\DesktopInfo.ini') -Force
                $startup = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\Startup'
                $shell = New-Object -ComObject WScript.Shell
                $shortcut = $shell.CreateShortcut((Join-Path $startup 'ITSETI Desktop Info.lnk'))
                $shortcut.TargetPath = Join-Path $env:WINDIR 'System32\wscript.exe'
                $shortcut.Arguments = '"'+(Join-Path $target 'start-panel.vbs')+'"'
                $shortcut.WorkingDirectory = $target
                $shortcut.Save()
            }
        } catch { $failures += ([string]$package.key + ': ' + $_.Exception.Message) }
    }
    if ($failures.Count) { throw ($failures -join [Environment]::NewLine) }
    if (Test-Path -LiteralPath (Join-Path $root 'server-software-error.txt')) { Remove-Item -LiteralPath (Join-Path $root 'server-software-error.txt') -Force }
} catch {
    [IO.File]::WriteAllText((Join-Path $root 'server-software-error.txt'),$_.Exception.Message,[Text.Encoding]::UTF8)
    throw
} finally {
    if ($locked) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
