param([string]$Folder,[int]$Days=7)
$ErrorActionPreference='Stop'
$index=0
foreach($log in @('System','Application')) {
    foreach($levels in @(@(1),@(2,3))) {
        $result=@{Events=@();Limited=$false;Error=$null}
        try {
            $rows=@(Get-WinEvent -FilterHashtable @{LogName=$log;StartTime=(Get-Date).AddDays(-$Days);Level=$levels} -MaxEvents 300 -ErrorAction Stop)
            $result.Events=@($rows | Select-Object LogName,ProviderName,Id,Level,TimeCreated,RecordId,@{Name='Message';Expression={''}})
            $result.Limited=($rows.Count -eq 300)
        } catch {if($_.FullyQualifiedErrorId -notlike 'NoMatchingEventsFound*'){$result.Error=$log+': '+$_.Exception.Message}}
        $temp=Join-Path $Folder 'partial.tmp'
        $result | Export-Clixml -LiteralPath $temp
        Move-Item -LiteralPath $temp -Destination (Join-Path $Folder ([string]$index+'.xml'))
        # Save counts first: a slow provider must not discard already collected events.
        if(!$result.Error -and $result.Events.Count) {
            $groups=$rows | Group-Object ProviderName,Id,Level | Sort-Object @{Expression={($_.Group | Measure-Object Level -Minimum).Minimum};Ascending=$true},@{Expression='Count';Descending=$true} | Select-Object -First 3
            foreach($group in $groups) {
                $example=$group.Group | Sort-Object TimeCreated -Descending | Select-Object -First 1
                try {
                    $message=$example.FormatDescription()
                    if(!$message){$message='Event provider returned no description. Data: '+(($example.Properties | ForEach-Object {$_.Value}) -join '; ')}
                } catch {$message='Event description unavailable: '+$_.Exception.Message}
                foreach($row in $result.Events){if($row.RecordId -eq $example.RecordId){$row.Message=$message;break}}
                $result | Export-Clixml -LiteralPath $temp
                [IO.File]::Replace($temp,(Join-Path $Folder ([string]$index+'.xml')),(Join-Path $Folder ([string]$index+'.previous')))
            }
        }
        $index++
    }
}
