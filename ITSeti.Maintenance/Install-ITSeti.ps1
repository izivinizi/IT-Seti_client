$ErrorActionPreference = 'Stop'
$setup = Join-Path $PSScriptRoot 'ITSeti-Maintenance-Setup.exe'
$credentialFile = Join-Path (Split-Path -Parent $PSScriptRoot) 'admin-credentials.private.xml'
if (!(Test-Path -LiteralPath $credentialFile -PathType Leaf)) {
    $credentialFile = Join-Path $PSScriptRoot 'admin-credentials.private.xml'
}

function Start-AdministratorHelper([string]$PowerShell,[string]$Helper,[pscredential]$Credential) {
    $start = New-Object Diagnostics.ProcessStartInfo $PowerShell
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WorkingDirectory = Join-Path $env:WINDIR 'System32'
    $start.UserName = $Credential.GetNetworkCredential().UserName
    $start.Domain = $env:COMPUTERNAME
    $start.Password = $Credential.Password
    $start.Arguments = '-NoProfile -ExecutionPolicy Bypass -File "' + $Helper + '"'
    $process = [Diagnostics.Process]::Start($start)
    if (!$process) { throw 'Windows did not start the administrator helper.' }
    try {
        $process.WaitForExit()
        return [pscustomobject]@{ ExitCode = $process.ExitCode }
    } finally { $process.Dispose() }
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
        Write-Host 'Saved administrator credentials were not found. Windows will request administrator approval.'
        $installer = Start-Process -FilePath $setup -Verb RunAs -WorkingDirectory (Split-Path -Parent $setup) -Wait -PassThru -ErrorAction Stop
        exit $installer.ExitCode
    }
    try {
    $xml = New-Object Xml.XmlDocument
    $xml.XmlResolver = $null
    $xml.Load([IO.Path]::GetFullPath($credentialFile))
    $requestedName = [string]$xml.credentials.username
    $password = [string]$xml.credentials.password
    if ($requestedName -notmatch '^[a-zA-Z0-9._-]+$' -or !$password) { throw 'Saved administrator credentials are invalid.' }

    $secret = ConvertTo-SecureString $password -AsPlainText -Force
    $credential = New-Object Management.Automation.PSCredential (($env:COMPUTERNAME + '\' + $requestedName), $secret)
    Write-Host "Starting installer as $env:COMPUTERNAME\$requestedName. Windows may ask for UAC confirmation."
    $secondaryLogon=Get-Service -Name 'seclogon' -ErrorAction Stop
    if($secondaryLogon.StartType -eq 'Disabled'){
        throw 'Windows Secondary Logon service is disabled by policy; stored credentials cannot launch the installer until an administrator enables it.'
    }
        # A credential logon has a filtered token. Elevate inside that account.
        $helper = Join-Path $PSScriptRoot 'Install-ITSeti-Admin.ps1'
        if (!(Test-Path -LiteralPath $helper -PathType Leaf)) { throw 'Administrator launch helper is missing.' }
        $powershell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
        Write-Host 'Waiting for Windows to validate the local account and launch the installer...'
        $installer = Start-AdministratorHelper -PowerShell $powershell -Helper $helper -Credential $credential
        if ($installer.ExitCode -eq 9001) { throw 'Administrator helper could not launch the installer.' }
        if ($installer.ExitCode -ne 0) { exit $installer.ExitCode }
    } catch {
        Write-Host ("Не удалось запустить установщик от сохранённой учётной записи: " + $_.Exception.Message)
        Write-Host 'Запускаю установщик через стандартное подтверждение UAC Windows.'
        $installer = Start-Process -FilePath $setup -Verb RunAs `
            -WorkingDirectory (Split-Path -Parent $setup) `
            -Wait -PassThru -ErrorAction Stop
    }
    if ($installer.ExitCode -ne 0) { throw "Installer did not complete (code $($installer.ExitCode))." }
    Write-Host 'Installation completed.'
    exit 0
}
catch {
    $exception=$_.Exception.GetBaseException()
    $nativeCode=if($exception -is [ComponentModel.Win32Exception]){$exception.NativeErrorCode}else{$null}
    $details='Installation failed: '+$exception.GetType().FullName+': '+$exception.Message
    if($nativeCode -ne $null){$details+=' (Windows error '+$nativeCode+')'}
    if($_.FullyQualifiedErrorId){$details+=' ['+$_.FullyQualifiedErrorId+']'}
    Write-Host $details -ForegroundColor Red
    exit 1
}
