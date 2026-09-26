$ErrorActionPreference='Stop'
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
    $note=if($created){'создана'}else{'уже была; пароль не менялся'}
    [Console]::Out.WriteLine("Локальная учётная запись $env:COMPUTERNAME\$name $note, права администратора подтверждены.")
} catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
