function Set-ScheduledFullBaseline([string]$Base, [DateTimeOffset]$When) {
    $target=Join-Path $Base 'last-full-run.txt'
    $temporary=$target+'.'+[guid]::NewGuid().ToString('N')+'.tmp'
    try {
        [IO.File]::WriteAllText($temporary,$When.ToUniversalTime().ToString('O'),[Text.Encoding]::ASCII)
        Move-Item -LiteralPath $temporary -Destination $target -Force
    } finally {
        if(Test-Path -LiteralPath $temporary){Remove-Item -LiteralPath $temporary -Force}
    }
}

function Initialize-ScheduledFullBaseline([string]$Base, [DateTimeOffset]$Now = [DateTimeOffset]::UtcNow) {
    $marker=Join-Path $Base 'last-full-run.txt'
    $last=[DateTimeOffset]::MinValue
    if((Test-Path -LiteralPath $marker -PathType Leaf) -and
       [DateTimeOffset]::TryParse([IO.File]::ReadAllText($marker),[ref]$last) -and
       $last -le $Now.AddMinutes(5)) {return $last.ToUniversalTime()}

    $baseline=$Now.ToUniversalTime()
    $latest=Join-Path $Base 'latest-full.txt'
    if(Test-Path -LiteralPath $latest -PathType Leaf) {
        try {
            $saved=[IO.File]::ReadAllText($latest).Trim()
            $id=Split-Path $saved -Leaf
            $parsed=[guid]::Empty
            $expected=Join-Path (Join-Path $Base 'Runs') $id
            if([guid]::TryParseExact($id,'N',[ref]$parsed) -and
               [IO.Path]::GetFullPath($saved) -eq [IO.Path]::GetFullPath($expected)) {
                $result=Join-Path $expected 'result.json'
                if(Test-Path -LiteralPath $result -PathType Leaf) {
                    $captured=[DateTimeOffset]([IO.File]::GetLastWriteTimeUtc($result))
                    if($captured -le $Now.AddMinutes(5)){$baseline=$captured.ToUniversalTime()}
                }
            }
        } catch { }
    }
    Set-ScheduledFullBaseline $Base $baseline
    return $baseline
}

function Test-ScheduledFullDue([string]$Base, [DateTimeOffset]$Now = [DateTimeOffset]::UtcNow) {
    $last=Initialize-ScheduledFullBaseline $Base $Now
    return ($Now.ToUniversalTime()-$last) -ge [TimeSpan]::FromDays(60)
}

function Complete-ScheduledFullCheck([string]$Base,[string]$Run,[string]$RepairScript) {
    if(!(Test-Path -LiteralPath (Join-Path $Run 'result.json') -PathType Leaf)) {
        throw 'Scheduled full diagnostic did not produce a result.'
    }
    $report=Join-Path $Run 'scheduled-repair.json'
    & $RepairScript -ResultFile $report
    if(!(Test-Path -LiteralPath $report -PathType Leaf)) {
        throw 'Scheduled system repair did not produce a result.'
    }
    $outcome=[IO.File]::ReadAllText($report,[Text.Encoding]::UTF8) | ConvertFrom-Json
    if(!$outcome.Success){throw ('Scheduled system repair failed: '+$outcome.Status)}
    Set-ScheduledFullBaseline $Base ([DateTimeOffset]::UtcNow)
}
