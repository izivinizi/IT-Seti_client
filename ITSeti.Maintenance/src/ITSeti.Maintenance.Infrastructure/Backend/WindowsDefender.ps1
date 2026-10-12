param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Status', 'Enable', 'Disable')]
    [string]$Action,
    [string]$StatusPath = (Join-Path $env:ProgramData 'ITSeti\Maintenance\WindowsDefender\status.json')
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
function Write-Status($Value) {
    $json = $Value | ConvertTo-Json -Compress
    $directory = Split-Path -Parent $StatusPath
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $temporary = $StatusPath + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
    try {
        [IO.File]::WriteAllText($temporary, $json, (New-Object Text.UTF8Encoding($false)))
        Move-Item -LiteralPath $temporary -Destination $StatusPath -Force
    } finally { Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue }
    $json
}

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

    Write-Status ([pscustomobject]@{
        Success = $true
        Available = $active
        RealTimeProtectionEnabled = if ($active) { [bool]$status.RealTimeProtectionEnabled } else { $null }
        TamperProtectionEnabled = $tamperProtected
        Mode = $mode
        Error = $null
    })
}
catch {
    Write-Status ([pscustomobject]@{
        Success = $false
        Available = $false
        RealTimeProtectionEnabled = $null
        TamperProtectionEnabled = $null
        Mode = ''
        Error = $_.Exception.Message
    })
    exit 1
}
