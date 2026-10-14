$ErrorActionPreference='Stop'
$utf8=New-Object System.Text.UTF8Encoding($false)
[Console]::InputEncoding=$utf8
[Console]::OutputEncoding=$utf8
try {
    $principal=[Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    if(!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Требуются права администратора Windows.'}
    $password=[Console]::ReadLine()
    if([string]::IsNullOrWhiteSpace($password)){throw 'Пароль не указан.'}
    $admin=Get-LocalUser -Name 'Admin' -ErrorAction SilentlyContinue
    $name=if($admin){'it-seti'}else{'Admin'}
    $account=Get-LocalUser -Name $name -ErrorAction SilentlyContinue
    $created=$false
    if(!$account){
        $secure=ConvertTo-SecureString $password -AsPlainText -Force
        $account=New-LocalUser -Name $name -Password $secure -PasswordNeverExpires -AccountNeverExpires -Description 'ИТ-Сети'
        $created=$true
    }
    $password=$null
    if(!$account.Enabled){Enable-LocalUser -SID $account.SID}
    $groupSid='S-1-5-32-544'
    $members=@(Get-LocalGroupMember -SID $groupSid -ErrorAction SilentlyContinue)
    if(!($members | Where-Object { $_.SID -eq $account.SID })){
        Add-LocalGroupMember -SID $groupSid -Member $account.SID.Value -ErrorAction Stop
    }
    $confirmed=@(Get-LocalGroupMember -SID $groupSid -ErrorAction Stop)
    if(!($confirmed | Where-Object { $_.SID -eq $account.SID })){
        throw "Не удалось подтвердить права администратора для $name."
    }
    $machineSid=$account.SID.AccountDomainSid.Value
    $builtInAdministratorSid=$machineSid+'-500'
    $removed=@()
    foreach($member in $confirmed){
        $memberDomainSid=$member.SID.AccountDomainSid
        if($member.ObjectClass -ne 'User' -or $null -eq $memberDomainSid -or
           $memberDomainSid.Value -ne $machineSid -or $member.SID.Value -in @($account.SID.Value,$builtInAdministratorSid)) {continue}
        Remove-LocalGroupMember -SID $groupSid -Member $member.SID.Value -ErrorAction Stop
        $removed += $member.Name
    }
    $remaining=@(Get-LocalGroupMember -SID $groupSid -ErrorAction Stop)
    if($remaining | Where-Object { $_.ObjectClass -eq 'User' -and $_.SID.AccountDomainSid -and
        $_.SID.AccountDomainSid.Value -eq $machineSid -and
        $_.SID.Value -notin @($account.SID.Value,$builtInAdministratorSid) }){
        throw 'Не удалось удалить все остальные локальные пользовательские учётные записи из группы администраторов.'
    }
    $auditPath=Join-Path $env:ProgramData 'ITSeti\Maintenance\local-admin-cleanup.log'
    $audit=New-Object 'System.Collections.Generic.List[string]'
    $audit.Add(([DateTimeOffset]::UtcNow.ToString('O')+' KEPT '+$name))
    foreach($removedName in $removed){$audit.Add(([DateTimeOffset]::UtcNow.ToString('O')+' REMOVED '+$removedName))}
    [IO.File]::AppendAllLines($auditPath,$audit,[Text.Encoding]::UTF8)
    $note=if($created){'создана'}else{'уже была; пароль не менялся'}
    [Console]::Out.WriteLine("Локальная учётная запись $env:COMPUTERNAME\$name $note, права администратора подтверждены. Остальные локальные учётки удалены из группы; встроенный Administrator и доменные группы сохранены.")
} catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
