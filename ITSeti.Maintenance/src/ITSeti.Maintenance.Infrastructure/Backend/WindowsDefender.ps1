param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Status', 'Enable', 'Disable')]
    [string]$Action
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)

try {
    $status = Get-MpComputerStatus
    $active = [bool]$status.AMServiceEnabled -and [bool]$status.AntivirusEnabled

    if ($Action -ne 'Status') {
        if (!$active) { throw 'DEFENDER_NOT_ACTIVE' }
        Set-MpPreference -DisableRealtimeMonitoring ($Action -eq 'Disable')
        $status = Get-MpComputerStatus
        $active = [bool]$status.AMServiceEnabled -and [bool]$status.AntivirusEnabled
    }

    $tamperProtected = $false
    $tamperProperty = $status.PSObject.Properties['IsTamperProtected']
    if ($null -ne $tamperProperty) { $tamperProtected = [bool]$tamperProperty.Value }
    $mode = ''
    $modeProperty = $status.PSObject.Properties['AMRunningMode']
    if ($null -ne $modeProperty) { $mode = [string]$modeProperty.Value }

    [pscustomobject]@{
        Success = $true
        Available = $active
        RealTimeProtectionEnabled = if ($active) { [bool]$status.RealTimeProtectionEnabled } else { $null }
        TamperProtectionEnabled = $tamperProtected
        Mode = $mode
        Error = $null
    } | ConvertTo-Json -Compress
}
catch {
    [pscustomobject]@{
        Success = $false
        Available = $false
        RealTimeProtectionEnabled = $null
        TamperProtectionEnabled = $null
        Mode = ''
        Error = $_.Exception.Message
    } | ConvertTo-Json -Compress
    exit 1
}
