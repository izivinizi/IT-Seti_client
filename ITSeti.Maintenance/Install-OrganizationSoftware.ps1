param([string]$InstallerDirectory,[string]$ResultFile,[switch]$PreflightOnly)
$ErrorActionPreference='Stop'
$auditedScriptHash='D15BA9507807B6B27094634952A59F3204D954AC6EB88F5180B0BCECE062355D'
$packages=@('AnyDesk-installer.exe','Host-IT-SETI.RMS.7.7.3.0v3.msi','OCS-Agent-Installerv4.exe','DesktopInfo3230.exe')

function Save-Result([string]$Text){
    if($ResultFile){[IO.File]::WriteAllText($ResultFile,$Text,[Text.Encoding]::ASCII)}
}

try {
    $candidates=New-Object Collections.Generic.List[string]
    if($InstallerDirectory){
        $candidates.Add((Join-Path $InstallerDirectory 'ITSETI-Setup'))
        $candidates.Add($InstallerDirectory)
    }
    $drives=@([IO.DriveInfo]::GetDrives() | Where-Object {$_.DriveType -eq 'Removable' -or $_.DriveType -eq 'Fixed'} | Sort-Object @{Expression={if($_.DriveType -eq 'Removable'){0}else{1}}})
    foreach($drive in $drives){
        try {if($drive.IsReady){$candidates.Add((Join-Path $drive.RootDirectory.FullName 'ITSETI-Setup'))}} catch {}
    }
    $source=@($candidates | Where-Object {Test-Path -LiteralPath (Join-Path $_ 'system\Install.ps1') -PathType Leaf} | Select-Object -First 1)
    if(!$source.Count){Save-Result 'ITSETI-Setup not found next to installer or at drive root.';exit 2}
    $script=Join-Path $source[0] 'system\Install.ps1'
    $actualHash=(Get-FileHash -LiteralPath $script -Algorithm SHA256).Hash
    $problems=New-Object Collections.Generic.List[string]
    if($actualHash -ne $auditedScriptHash){$problems.Add('Install.ps1 changed since audit.')}
    foreach($name in $packages){
        $path=Join-Path $source[0] ('system\packages\'+$name)
        if(!(Test-Path -LiteralPath $path -PathType Leaf)){$problems.Add($name+': missing');continue}
        $signature=Get-AuthenticodeSignature -LiteralPath $path
        if($signature.Status -ne 'Valid'){$problems.Add($name+': '+$signature.Status)}
    }
    if($problems.Count){Save-Result ($problems -join "`r`n");exit 2}
    if($PreflightOnly){Save-Result 'READY';exit 0}
    $admin=([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if(!$admin){Save-Result 'Administrator rights are required.';exit 2}
    $process=Start-Process -FilePath (Join-Path $PSHOME 'powershell.exe') -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',('"'+$script+'"'),'-Silent') -WindowStyle Hidden -PassThru -Wait
    if($process.ExitCode -ne 0){Save-Result ('Install.ps1 exit code: '+$process.ExitCode+'. See C:\ProgramData\ITSETI\install.log.');exit 2}
    Save-Result 'OK'
    exit 0
} catch {
    Save-Result ('Setup error: '+$_.Exception.Message)
    exit 2
}
