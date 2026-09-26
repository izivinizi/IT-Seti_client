param(
    [string]$InstallerDirectory,
    [string]$ResultFile,
    [switch]$PreflightOnly,
    [ValidateSet('AnyDesk', 'RMS', 'OCS', 'Panel')][string]$Component
)
$ErrorActionPreference = 'Stop'
$auditedScriptHash = 'D15BA9507807B6B27094634952A59F3204D954AC6EB88F5180B0BCECE062355D'
$packages = @{
    AnyDesk = @{ File = 'AnyDesk-installer.exe'; Hash = 'B9AD79EAF7A4133F95F24C3B9D976C72F34264DC5C99030F0E57992CB5621F78' }
    RMS = @{ File = 'Host-IT-SETI.RMS.7.7.3.0v3.msi'; Hash = '9A8E4096C155B7432BDC35F5079169B4F496D4E4A0AAC943DA44B5E877E27ABF' }
    OCS = @{ File = 'OCS-Agent-Installerv4.exe'; Hash = 'EA1239BD75C43A85F89BD5813B64D9D0789F8E598EA24F29B2B28CE1F012ED3D' }
    Panel = @{ File = 'DesktopInfo3230.exe'; Hash = 'BE653BF81088855640BD7F2D42D682BCB2731C71F390A83928D68AB6E707021C' }
}
$panelHashes = @{
    'DesktopInfo.ini' = 'C5BEEA181AB32B4677C7478D6B2B0026969881DE11E7167077085D5A07A85D23'
    'update-support-ids.ps1' = '61492DCC75D988778E12925C8CB0F4867C15BEC8B022E6EEA31C9B5BBE7C8617'
    'start-panel.vbs' = 'BBB7894DFEF424A161BCEE4C058AFA64B7C949B5C0074DC937CE56476932C8B5'
}

function Save-Result([string]$Text) {
    if ($ResultFile) {
        $resultDirectory = Split-Path -Parent $ResultFile
        if ($resultDirectory) { New-Item -ItemType Directory -Path $resultDirectory -Force | Out-Null }
        [IO.File]::WriteAllText($ResultFile, $Text, [Text.UTF8Encoding]::new($false))
    }
}

function Write-InstallLog([string]$Message) {
    $directory = Join-Path $env:ProgramData 'ITSETI'
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    Add-Content -LiteralPath (Join-Path $directory 'install.log') -Value ('{0:yyyy-MM-dd HH:mm:ss}  {1}' -f (Get-Date), $Message) -Encoding UTF8
}

function Find-SetupSource {
    $candidates = New-Object Collections.Generic.List[string]
    if ($InstallerDirectory) {
        $candidates.Add((Join-Path $InstallerDirectory 'ITSETI-Setup'))
        $candidates.Add($InstallerDirectory)
    }
    $drives = @([IO.DriveInfo]::GetDrives() | Where-Object { $_.DriveType -eq 'Removable' -or $_.DriveType -eq 'Fixed' } |
        Sort-Object @{ Expression = { if ($_.DriveType -eq 'Removable') { 0 } else { 1 } } })
    foreach ($drive in $drives) {
        try { if ($drive.IsReady) { $candidates.Add((Join-Path $drive.RootDirectory.FullName 'ITSETI-Setup')) } } catch { }
    }
    return @($candidates | Where-Object { Test-Path -LiteralPath (Join-Path $_ 'system\Install.ps1') -PathType Leaf } | Select-Object -First 1)
}

function Test-PinnedFile([string]$Path, [string]$ExpectedHash) {
    if (!(Test-Path -LiteralPath $Path -PathType Leaf)) { return $false }
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -eq $ExpectedHash
}

function Test-PanelFiles([string]$Root) {
    foreach ($name in $panelHashes.Keys) {
        $path = Join-Path $Root ('system\panel\' + $name)
        if (!(Test-Path -LiteralPath $path -PathType Leaf) -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $panelHashes[$name]) {
            throw "Panel file failed verification: $name"
        }
    }
}

function Invoke-Installer([string]$File, [string[]]$Arguments, [int]$TimeoutSeconds = 900) {
    $process = Start-Process -FilePath $File -ArgumentList $Arguments -WindowStyle Hidden -PassThru -ErrorAction Stop
    if (!$process.WaitForExit($TimeoutSeconds * 1000)) {
        try { $process.Kill() } catch { }
        throw "Installer timed out: $(Split-Path $File -Leaf)"
    }
    return $process.ExitCode
}

function Wait-For([scriptblock]$Condition, [int]$Seconds) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        if (& $Condition) { return $true }
        Start-Sleep -Seconds 2
    } while ([DateTime]::UtcNow -lt $deadline)
    return [bool](& $Condition)
}

function Get-RmsId {
    foreach ($key in @('HKLM:\SOFTWARE\TektonIT\RMS Host\Host\Parameters', 'HKLM:\SOFTWARE\WOW6432Node\TektonIT\RMS Host\Host\Parameters')) {
        try {
            $blob = (Get-ItemProperty -Path $key -Name InternetId -ErrorAction Stop).InternetId
            $xml = [Text.Encoding]::UTF8.GetString($blob)
            if ($xml -match '<internet_id>([^<]+)</internet_id>') { return $Matches[1].Trim() }
        } catch { }
    }
    return ''
}

function Install-Component([string]$Name, [string]$Root) {
    $pkgRoot = Join-Path $Root 'system\packages'
    $programFiles = $env:ProgramFiles
    $programFiles86 = if (${env:ProgramFiles(x86)}) { ${env:ProgramFiles(x86)} } else { $programFiles }
    switch ($Name) {
        'AnyDesk' {
            $exe = @("$programFiles\AnyDesk\AnyDesk.exe", "$programFiles86\AnyDesk\AnyDesk.exe") |
                Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
            if (!$exe) {
                $installer = Join-Path $pkgRoot $packages.AnyDesk.File
                $code = Invoke-Installer $installer @('--install', ('"' + "$programFiles86\AnyDesk" + '"'), '--silent', '--start-with-win') 600
                if (!(Wait-For { Test-Path -LiteralPath "$programFiles86\AnyDesk\AnyDesk.exe" } 60)) { throw "AnyDesk installation failed (code $code)." }
                $exe = "$programFiles86\AnyDesk\AnyDesk.exe"
            }
            $config = Join-Path $env:ProgramData 'AnyDesk\system.conf'
            if (Wait-For { Test-Path -LiteralPath $config } 30) {
                $required = [ordered]@{ 'ad.security.update_type' = '1'; 'ad.security.interactive_access' = '0' }
                $lines = @(Get-Content -LiteralPath $config)
                $seen = @{}
                $updated = foreach ($line in $lines) {
                    $key = ($line -split '=', 2)[0]
                    if ($required.Contains($key)) { $seen[$key] = $true; "$key=$($required[$key])" } else { $line }
                }
                foreach ($key in $required.Keys) { if (!$seen.ContainsKey($key)) { $updated += "$key=$($required[$key])" } }
                Set-Content -LiteralPath $config -Value $updated -Encoding ASCII
                Restart-Service -Name AnyDesk -Force -ErrorAction SilentlyContinue
            }
            return 'AnyDesk установлен и настроен.'
        }
        'RMS' {
            $msi = Join-Path $pkgRoot $packages.RMS.File
            $logRoot = Join-Path $env:ProgramData 'ITSETI'
            New-Item -ItemType Directory -Path $logRoot -Force | Out-Null
            $oldId = Get-RmsId
            $log = Join-Path $logRoot 'rms-install.log'
            $code = Invoke-Installer (Join-Path $env:WINDIR 'System32\msiexec.exe') @('/i', ('"' + $msi + '"'), '/qn', '/norestart', 'REBOOT=ReallySuppress', '/l*v', $log) 900
            if ($code -notin 0, 3010, 1641) { throw "RMS installation failed (msiexec $code). See $log" }
            $serviceReady = Wait-For { (Get-Service -Name RManService -ErrorAction SilentlyContinue).Status -eq 'Running' } 90
            $null = Wait-For { Get-RmsId } 120
            $newId = Get-RmsId
            $note = if (!$serviceReady) { ' Установка завершилась, но служба не запустилась.' } else { '' }
            if ($oldId -and $newId -and $oldId -ne $newId) { $note += " Внимание: Internet-ID изменился с $oldId на $newId." }
            if ($code -eq 3010) { $note += ' Требуется перезагрузка.' }
            return ('RMS Host установлен.' + $note)
        }
        'OCS' {
            $service = Get-Service -Name 'OCS Inventory Service' -ErrorAction SilentlyContinue
            if (!$service) {
                $installer = Join-Path $pkgRoot $packages.OCS.File
                $code = Invoke-Installer $installer @('/S') 900
                if (!(Wait-For { Get-Service -Name 'OCS Inventory Service' -ErrorAction SilentlyContinue } 90)) { throw "OCS installation failed (code $code)." }
            }
            $inventory = Join-Path $programFiles 'OCS Inventory Agent\OCSInventory.exe'
            if (Test-Path -LiteralPath $inventory) { Start-Process -FilePath $inventory -ArgumentList @('/NOW', '/NOTAG', '/NOSPLASH') -WindowStyle Hidden | Out-Null }
            return 'OCS Inventory установлен; инвентаризация запущена.'
        }
        'Panel' {
            $installRoot = Join-Path $programFiles 'Desktop Info'
            $exe = Join-Path $installRoot 'DesktopInfo.exe'
            $installedVersion = if (Test-Path -LiteralPath $exe) {
                try { [version](Get-Item -LiteralPath $exe).VersionInfo.ProductVersion } catch { [version]'0.0' }
            } else { [version]'0.0' }
            if ($installedVersion -lt [version]'3.23.0') {
                $installer = Join-Path $pkgRoot $packages.Panel.File
                $code = Invoke-Installer $installer @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/NOCANCEL', '/CLOSEAPPLICATIONS', ('/DIR="' + $installRoot + '"')) 600
                if ($code -notin 0, 3010) { throw "Desktop Info installer failed (code $code)." }
                if (!(Wait-For { Test-Path -LiteralPath $exe } 60)) { throw "Desktop Info installation failed (code $code)." }
            }
            $data = Join-Path $env:ProgramData 'ITSETI'
            New-Item -ItemType Directory -Path $data -Force | Out-Null
            foreach ($name in $panelHashes.Keys) { Copy-Item -LiteralPath (Join-Path $Root ('system\panel\' + $name)) -Destination (Join-Path $data $name) -Force }
            Copy-Item -LiteralPath (Join-Path $data 'DesktopInfo.ini') -Destination (Join-Path $installRoot 'DesktopInfo.ini') -Force
            $startup = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\Startup'
            New-Item -ItemType Directory -Path $startup -Force | Out-Null
            $shortcutPath = Join-Path $startup 'ITSETI Desktop Info.lnk'
            $shell = New-Object -ComObject WScript.Shell
            $shortcut = $shell.CreateShortcut($shortcutPath)
            $shortcut.TargetPath = Join-Path $env:WINDIR 'System32\wscript.exe'
            $shortcut.Arguments = '"' + (Join-Path $data 'start-panel.vbs') + '"'
            $shortcut.WorkingDirectory = $data
            $shortcut.IconLocation = "$exe,0"
            $shortcut.Description = 'Информационная панель ИТ сети'
            $shortcut.Save()
            return 'Desktop Info и панель ИТ-Сети установлены. Панель запустится при следующем входе пользователя.'
        }
    }
}

try {
    $source = Find-SetupSource
    if (!$source.Count) { Save-Result 'Комплект ITSETI-Setup не найден.'; exit 2 }
    $source = [IO.Path]::GetFullPath([string]$source)
    $script = Join-Path $source 'system\Install.ps1'
    $actualScriptHash = (Get-FileHash -LiteralPath $script -Algorithm SHA256).Hash
    if ($actualScriptHash -ne $auditedScriptHash) { Save-Result 'Install.ps1 changed since audit.'; exit 2 }

    $selected = if ($Component) { @($Component) } else { @('AnyDesk', 'RMS', 'OCS', 'Panel') }
    foreach ($name in $selected) {
        $package = $packages[$name]
        $path = Join-Path $source ('system\packages\' + $package.File)
        if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "$($package.File): missing" }
        if (!(Test-PinnedFile $path $package.Hash)) { throw "$($package.File): signature/hash verification failed" }
    }
    if (!$Component -or $Component -eq 'Panel') { Test-PanelFiles $source }
    if ($PreflightOnly) { Save-Result 'READY'; exit 0 }
    $admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if (!$admin) { throw 'Administrator rights are required.' }

    if ($Component) {
        $detail = Install-Component $Component $source
        Save-Result $detail
        exit 0
    }

    $failures = New-Object Collections.Generic.List[string]
    foreach ($name in @('AnyDesk', 'RMS', 'OCS', 'Panel')) {
        try { Write-InstallLog ($name + ': ' + (Install-Component $name $source)) }
        catch {
            $failure = $name + ': ' + $_.Exception.Message
            $failures.Add($failure)
            Write-InstallLog ('ERROR ' + $failure)
        }
    }
    if ($failures.Count) { throw ($failures -join [Environment]::NewLine) }
    Save-Result 'OK'
    exit 0
} catch {
    Save-Result $_.Exception.Message
    exit 2
}
