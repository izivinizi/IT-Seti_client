param()
$ErrorActionPreference='Stop'

$script:HwInfoCachePath = Join-Path $env:ProgramData 'ITSeti\Maintenance\hwinfo.json'

function Read-HwInfoCache([int]$MaximumAgeSeconds=90) {
    if(!(Test-Path -LiteralPath $script:HwInfoCachePath -PathType Leaf)){return $null}
    try {
        $json=Get-Content -LiteralPath $script:HwInfoCachePath -Raw -Encoding UTF8 | ConvertFrom-Json
        $capturedValue=$json.CapturedAtUtc
        if($capturedValue -is [DateTimeOffset]){$captured=$capturedValue}
        elseif($capturedValue -is [DateTime]){$captured=[DateTimeOffset]$capturedValue}
        else {
            $captured=[DateTimeOffset]::MinValue
            if(![DateTimeOffset]::TryParse([string]$capturedValue,[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::RoundtripKind,[ref]$captured)){return $null}
        }
        $age=([DateTimeOffset]::UtcNow-$captured.ToUniversalTime()).TotalSeconds
        if($age -lt -60 -or $age -gt $MaximumAgeSeconds){return $null}
        return $json
    } catch {return $null}
}

function Get-HwInfoCpuLoadPercent {
    $snapshot=Read-HwInfoCache
    if($null -eq $snapshot -or $null -eq $snapshot.CpuLoadPercent){return $null}
    $value=0.0
    if([double]::TryParse([string]$snapshot.CpuLoadPercent,[Globalization.NumberStyles]::Float,[Globalization.CultureInfo]::InvariantCulture,[ref]$value) -and
       ![double]::IsNaN($value) -and ![double]::IsInfinity($value) -and $value -ge 0 -and $value -le 100){return $value}
    return $null
}

function Get-HwInfoCpuTemperatureC {
    $snapshot=Read-HwInfoCache
    if($null -eq $snapshot -or $null -eq $snapshot.CpuTemperatureC){return $null}
    $value=0.0
    if([double]::TryParse([string]$snapshot.CpuTemperatureC,[Globalization.NumberStyles]::Float,[Globalization.CultureInfo]::InvariantCulture,[ref]$value) -and $value -ge 5 -and $value -le 120){return $value}
    return $null
}
