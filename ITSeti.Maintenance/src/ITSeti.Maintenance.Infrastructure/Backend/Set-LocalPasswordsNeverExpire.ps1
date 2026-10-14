param([string]$LogPath)
$ErrorActionPreference='Stop'
if([string]::IsNullOrWhiteSpace($LogPath)){
    $LogPath=Join-Path $env:ProgramData 'ITSeti\Maintenance\local-password-policy.log'
}
$logRoot=Split-Path -Parent $LogPath
if(!(Test-Path -LiteralPath $logRoot)){New-Item -ItemType Directory -Path $logRoot -Force | Out-Null}
$lines=New-Object 'System.Collections.Generic.List[string]'
$results=New-Object 'System.Collections.Generic.List[object]'
$computer=[ADSI]('WinNT://'+$env:COMPUTERNAME+',computer')
foreach($account in $computer.Children){
    try{
        if([string]$account.SchemaClassName -ne 'User'){continue}
        $name=[string]$account.Name
        if([string]::IsNullOrWhiteSpace($name)){continue}
        $raw=$account.Properties['UserFlags'].Value
        if($null -eq $raw){
            $action='Failed';$message='UserFlags unavailable'
            $lines.Add(([DateTimeOffset]::UtcNow.ToString('O')+' ERROR '+$name+' '+$message))
            $results.Add([pscustomobject]@{Account=$name;Action=$action})
            continue
        }
        $flags=[int]$raw
        $neverExpires=0x10000
        if(($flags -band $neverExpires) -eq 0){
            $account.Put('UserFlags',($flags -bor $neverExpires))
            $account.SetInfo()
            $check=[ADSI]('WinNT://'+$env:COMPUTERNAME+'/'+$name+',user')
            if(([int]$check.UserFlags.Value -band $neverExpires) -eq 0){throw 'Windows did not confirm the updated flag'}
            $action='Updated'
            $lines.Add(([DateTimeOffset]::UtcNow.ToString('O')+' UPDATED '+$name))
        } else {
            $action='AlreadyCompliant'
        }
        $results.Add([pscustomobject]@{Account=$name;Action=$action})
    }catch{
        $name=[string]$account.Name
        $lines.Add(([DateTimeOffset]::UtcNow.ToString('O')+' ERROR '+$name+' '+$_.Exception.GetType().Name))
        $results.Add([pscustomobject]@{Account=$name;Action='Failed'})
    }
}
if($lines.Count -gt 0){[IO.File]::AppendAllLines($LogPath,$lines,[Text.Encoding]::UTF8)}
$results.ToArray()
