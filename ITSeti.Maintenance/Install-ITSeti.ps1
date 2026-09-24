$ErrorActionPreference = 'Stop'
$setup = Join-Path $PSScriptRoot 'ITSeti-Maintenance-Setup.exe'
$credentialFile = Join-Path (Split-Path -Parent $PSScriptRoot) 'admin-credentials.private.xml'
if (!(Test-Path -LiteralPath $credentialFile -PathType Leaf)) {
    $credentialFile = Join-Path $PSScriptRoot 'admin-credentials.private.xml'
}

try {
    if (!(Test-Path -LiteralPath $setup -PathType Leaf)) { throw 'Installer EXE is missing next to this script.' }

    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal $identity
    if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        Write-Host 'Administrator token is active. Starting installer.'
        $installer = Start-Process -FilePath $setup -Wait -PassThru
        exit $installer.ExitCode
    }

    if (!(Test-Path -LiteralPath $credentialFile -PathType Leaf)) {
        throw "Saved local administrator credentials are missing: $credentialFile"
    }
    $xml = New-Object Xml.XmlDocument
    $xml.XmlResolver = $null
    $xml.Load([IO.Path]::GetFullPath($credentialFile))
    $requestedName = [string]$xml.credentials.username
    $password = [string]$xml.credentials.password
    if ($requestedName -notmatch '^[a-zA-Z0-9._-]+$' -or !$password) { throw 'Saved administrator credentials are invalid.' }

    $accounts = @(Get-WmiObject Win32_UserAccount -Filter 'LocalAccount=True' -ErrorAction Stop |
        Where-Object { !$_.Disabled -and !$_.Lockout })
    $administrators = Get-WmiObject Win32_Group -Filter "LocalAccount=True AND SID='S-1-5-32-544'" -ErrorAction Stop
    if (!$administrators) { throw 'Local Administrators group was not found.' }
    $adminSids = @($administrators.GetRelated('Win32_UserAccount') | ForEach-Object { $_.SID })
    $account = $null
    foreach ($candidate in @($requestedName, 'Admin', 'it-seti') | Select-Object -Unique) {
        $found = @($accounts | Where-Object { $adminSids -contains $_.SID -and $_.Name -ieq $candidate })
        if ($found.Count -eq 1) { $account = $found[0]; break }
    }
    if (!$account) { throw 'Configured local administrator account was not found.' }

    $secret = ConvertTo-SecureString $password -AsPlainText -Force
    $credential = New-Object Management.Automation.PSCredential (($env:COMPUTERNAME + '\' + $account.Name), $secret)
    $helperScript = Join-Path $PSScriptRoot 'Install-ITSeti-Admin.ps1'
    if (!(Test-Path -LiteralPath $helperScript -PathType Leaf)) { throw 'Administrator launcher is missing next to this script.' }
    $arguments = '-NoProfile -ExecutionPolicy Bypass -File "' + $helperScript + '"'
    if ($arguments.Length -gt 950) { throw 'Installer path is too long for secondary logon.' }

    Write-Host "Starting as $env:COMPUTERNAME\$($account.Name). Windows may ask for UAC confirmation."
    $helper = Start-Process -FilePath (Join-Path $PSHOME 'powershell.exe') -Credential $credential `
        -LoadUserProfile -WorkingDirectory $env:windir `
        -ArgumentList $arguments `
        -Wait -PassThru -ErrorAction Stop
    if ($helper.ExitCode -ne 0) { throw "Installer did not complete (code $($helper.ExitCode))." }
    Write-Host 'Installation completed.'
    exit 0
}
catch {
    Write-Host ('Installation failed: ' + $_.Exception.Message) -ForegroundColor Red
    exit 1
}
