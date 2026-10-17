param([Parameter(Mandatory=$true)][ValidatePattern('^S-1-5-21-\d+-\d+-\d+-\d+$')][string]$Sid)
$ErrorActionPreference='Stop'
$root=Join-Path $env:ProgramData 'ITSeti\Maintenance\Accounts'
New-Item -ItemType Directory -Path $root -Force | Out-Null
$result=Join-Path $root ($Sid+'.json')
try {
    if([Security.Principal.WindowsIdentity]::GetCurrent().User.Value -ne 'S-1-5-18'){throw 'Account changes require SYSTEM.'}
    $user=Get-LocalUser -SID ([Security.Principal.SecurityIdentifier]$Sid) -ErrorAction Stop
    if($user.Name -in @('Admin','it-seti','Administrator','Администратор') -or $Sid -match '-500$'){throw 'Штатную инженерную учётную запись изменять нельзя.'}
    $group=Get-LocalGroup -SID 'S-1-5-32-544'
    $members=@(Get-LocalGroupMember -Group $group)
    if(!($members | Where-Object { $_.SID.Value -eq $Sid })){throw 'Учётная запись уже не входит в локальные администраторы.'}
    $remaining=@($members | Where-Object { $_.SID.Value -ne $Sid -and $_.ObjectClass -eq 'User' } | ForEach-Object {
        try {Get-LocalUser -SID $_.SID -ErrorAction Stop | Where-Object Enabled} catch { }
    })
    if($remaining.Count -eq 0){throw 'Нельзя убрать последнего включённого локального администратора.'}
    Remove-LocalGroupMember -Group $group -Member $user -ErrorAction Stop
    if(Get-LocalGroupMember -Group $group | Where-Object { $_.SID.Value -eq $Sid }){throw 'Windows не подтвердила изменение членства.'}
    $status=@{Success=$true;Message='Права администратора сняты. Новый токен прав действует после выхода из учётной записи.'}
} catch {$status=@{Success=$false;Message=$_.Exception.Message}}
$status.UpdatedAt=[DateTimeOffset]::UtcNow.ToString('O')
$temporary=$result+'.'+[Guid]::NewGuid().ToString('N')+'.tmp'
try {
    [IO.File]::WriteAllText($temporary,($status | ConvertTo-Json -Compress),[Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporary -Destination $result -Force
} finally {Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue}
if(!$status.Success){throw $status.Message}
