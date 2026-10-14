param([ValidateSet('Minimize','Restore')][string]$Action)
$ErrorActionPreference='Stop'
$root=Join-Path $env:ProgramData 'ITSeti\Maintenance\WindowsDiagnosticData'
$backup=Join-Path $root 'original-policy.json'
$statusPath=Join-Path $root 'policy-status.json'
$policyPath='HKLM:\SOFTWARE\Policies\Microsoft\Windows\DataCollection'
New-Item -ItemType Directory -Path $root -Force | Out-Null

function Write-Status([string]$State,[string]$Message) {
    $status=@{Action=$Action;State=$State;Message=$Message;UpdatedAt=[DateTimeOffset]::UtcNow.ToString('o')}
    $temporary=$statusPath+'.'+[guid]::NewGuid().ToString('N')+'.tmp'
    try {
        [IO.File]::WriteAllText($temporary,($status | ConvertTo-Json -Compress),[Text.Encoding]::UTF8)
        Move-Item -LiteralPath $temporary -Destination $statusPath -Force
    } finally {Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue}
}

try {
    $current=Get-ItemProperty -LiteralPath $policyPath -ErrorAction SilentlyContinue
    $hasValue=$null -ne $current -and $null -ne $current.AllowTelemetry
    if($Action -eq 'Minimize') {
        $edition=[string](Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' -Name EditionID -ErrorAction Stop).EditionID
        $desired=if($edition -match 'Enterprise|Education|Server'){0}else{1}
        if($hasValue -and [int]$current.AllowTelemetry -le $desired -and !(Test-Path -LiteralPath $backup)) {
            Write-Status 'AlreadyMinimum' 'Политика диагностики Windows уже задаёт минимальный уровень для этой редакции; исходная настройка не изменена.'
            return
        }
        if(!(Test-Path -LiteralPath $backup)) {
            $state=@{KeyExisted=[bool](Test-Path -LiteralPath $policyPath);ValueExisted=[bool]$hasValue;
                Value=$(if($hasValue){[int]$current.AllowTelemetry}else{0});Desired=$desired}
            $temporary=$backup+'.tmp'
            [IO.File]::WriteAllText($temporary,($state | ConvertTo-Json -Compress),[Text.Encoding]::UTF8)
            Move-Item -LiteralPath $temporary -Destination $backup -Force
        } else {
            $state=Get-Content -LiteralPath $backup -Raw -Encoding UTF8 | ConvertFrom-Json
            $desired=[int]$state.Desired
        }
        New-Item -Path $policyPath -Force | Out-Null
        New-ItemProperty -LiteralPath $policyPath -Name AllowTelemetry -PropertyType DWord -Value $desired -Force | Out-Null
        if([int](Get-ItemProperty -LiteralPath $policyPath -Name AllowTelemetry).AllowTelemetry -ne $desired){throw 'Windows не подтвердила изменение политики.'}
        if($desired -eq 0) {
            Write-Status 'Minimized' 'Запрошено отключение диагностических данных Windows (уровень 0). Доменная политика может переопределить настройку.'
        } else {
            Write-Status 'Minimized' 'Для этой редакции Windows полный отказ недоступен: установлен минимальный поддерживаемый уровень 1 (обязательные данные).'
        }
    } else {
        if(!(Test-Path -LiteralPath $backup)) {
            Write-Status 'NoSavedState' 'Исходная настройка не сохранена приложением; ничего не изменено.'
            return
        }
        $state=Get-Content -LiteralPath $backup -Raw -Encoding UTF8 | ConvertFrom-Json
        if(!$hasValue -or [int]$current.AllowTelemetry -ne [int]$state.Desired) {
            Write-Status 'ChangedExternally' 'Настройка изменилась извне; приложение не перезаписывало текущую политику.'
            return
        }
        if($state.ValueExisted) {
            New-ItemProperty -LiteralPath $policyPath -Name AllowTelemetry -PropertyType DWord -Value ([int]$state.Value) -Force | Out-Null
        } else {
            Remove-ItemProperty -LiteralPath $policyPath -Name AllowTelemetry -ErrorAction Stop
        }
        Remove-Item -LiteralPath $backup -Force
        Write-Status 'Restored' 'Исходная настройка диагностических данных Windows восстановлена.'
    }
} catch {
    Write-Status 'Failed' ('Не удалось изменить диагностические данные Windows: '+$_.Exception.Message)
    throw
}
