param([string]$LogPath)
$ErrorActionPreference='Stop'
if([string]::IsNullOrWhiteSpace($LogPath)){
    $LogPath=Join-Path $env:ProgramData 'ITSeti\Maintenance\local-password-policy.log'
}
$logRoot=Split-Path -Parent $LogPath
if(!(Test-Path -LiteralPath $logRoot)){New-Item -ItemType Directory -Path $logRoot -Force | Out-Null}
$lines=New-Object 'System.Collections.Generic.List[string]'
$computer=[ADSI]('WinNT://'+$env:COMPUTERNAME+',computer')
foreach($account in $computer.Children){
    try{
        if([string]$account.SchemaClassName -ne 'User'){continue}
        $name=[string]$account.Name
        $raw=$account.Properties['UserFlags'].Value
        if($null -eq $raw){$lines.Add(([DateTimeOffset]::UtcNow.ToString('O')+' SKIP '+$name+' UserFlags unavailable'));continue}
        $flags=[int]$raw
        $neverExpires=0x10000
        if(($flags -band $neverExpires) -eq 0){
            $account.Put('UserFlags',($flags -bor $neverExpires))
            $account.SetInfo()
            $lines.Add(([DateTimeOffset]::UtcNow.ToString('O')+' UPDATED '+$name))
        }
    }catch{
        $lines.Add(([DateTimeOffset]::UtcNow.ToString('O')+' ERROR '+[string]$account.Name+' '+$_.Exception.GetType().Name))
    }
}
if($lines.Count -gt 0){[IO.File]::AppendAllLines($LogPath,$lines,[Text.Encoding]::UTF8)}
