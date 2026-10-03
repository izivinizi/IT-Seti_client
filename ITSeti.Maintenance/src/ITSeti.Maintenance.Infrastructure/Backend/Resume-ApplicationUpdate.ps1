param([int]$ParentPid)
if($ParentPid -le 0){exit 1}
$deadline=[DateTime]::UtcNow.AddMinutes(2)
while((Get-Process -Id $ParentPid -ErrorAction SilentlyContinue) -and [DateTime]::UtcNow -lt $deadline){
    Start-Sleep -Milliseconds 500
}
if(Get-Process -Id $ParentPid -ErrorAction SilentlyContinue){exit 1}
Start-Sleep -Seconds 1
$statusPath=Join-Path $env:ProgramData 'ITSeti\Maintenance\Updates\last-result.txt'
$scheduler=Join-Path $env:WINDIR 'System32\schtasks.exe'
for($attempt=0;$attempt -lt 40;$attempt++){
    $status=''
    try {if(Test-Path -LiteralPath $statusPath -PathType Leaf){$status=[IO.File]::ReadAllText($statusPath)}} catch [IO.IOException] {}
    if($status -match 'Версия \d+\.\d+\.\d+ установлена|обновление не требуется'){exit 0}
    if($status -match 'Ошибка обновления:'){exit 1}
    if($status -match 'Пакет загружен и проверен\.'){
        & $scheduler /Run /TN 'ITSeti-Maintenance-Update' | Out-Null
        if($LASTEXITCODE -ne 0){exit $LASTEXITCODE}
    }
    Start-Sleep -Seconds 3
}
exit 1
