param([ValidateSet('Disable','Restore')][string]$Action)
$ErrorActionPreference = 'Stop'
$root = Join-Path $env:ProgramData 'ITSeti\Maintenance\WindowsUpdate'
$statePath = Join-Path $root 'original-policy.json'
$resultPath = Join-Path $root 'policy-status.json'
$policyPath = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU'
New-Item -ItemType Directory -Path $root -Force | Out-Null

function Save-Status([string]$State, [string]$Message) {
    $result = @{ Action = $Action; State = $State; Message = $Message; UpdatedAt = [DateTimeOffset]::Now.ToString('o') }
    $pending = $resultPath + '.pending'
    $result | ConvertTo-Json -Compress | Set-Content -LiteralPath $pending -Encoding UTF8
    Move-Item -LiteralPath $pending -Destination $resultPath -Force
}

try {
    if ($Action -eq 'Disable') {
        $key = Get-ItemProperty -LiteralPath $policyPath -ErrorAction SilentlyContinue
        if ($key -and $null -ne $key.NoAutoUpdate -and [int]$key.NoAutoUpdate -eq 1 -and !(Test-Path -LiteralPath $statePath)) {
            Save-Status 'AlreadyDisabled' 'Автообновления уже отключены существующей политикой Windows; исходное значение приложения не менялось.'
            return
        }
        if (!(Test-Path -LiteralPath $statePath)) {
            $keyExisted = Test-Path -LiteralPath $policyPath
            $valueExisted = $key -and $null -ne $key.NoAutoUpdate
            $state = @{ KeyExisted = [bool]$keyExisted; ValueExisted = [bool]$valueExisted; Value = $(if ($valueExisted) { [int]$key.NoAutoUpdate } else { 0 }) }
            $state | ConvertTo-Json -Compress | Set-Content -LiteralPath ($statePath + '.pending') -Encoding UTF8
            Move-Item -LiteralPath ($statePath + '.pending') -Destination $statePath -Force
        }
        New-Item -Path $policyPath -Force | Out-Null
        New-ItemProperty -LiteralPath $policyPath -Name NoAutoUpdate -PropertyType DWord -Value 1 -Force | Out-Null
        if ([int](Get-ItemProperty -LiteralPath $policyPath).NoAutoUpdate -ne 1) { throw 'Windows не подтвердила настройку отключения.' }
        Save-Status 'Disabled' 'Автоматическая установка обновлений отключена. Проверяйте и устанавливайте обновления вручную.'
    } else {
        if (!(Test-Path -LiteralPath $statePath)) {
            Save-Status 'NoSavedState' 'Исходная настройка не сохранена приложением; ничего не изменено.'
            return
        }
        $state = Get-Content -LiteralPath $statePath -Raw -Encoding UTF8 | ConvertFrom-Json
        $key = Get-ItemProperty -LiteralPath $policyPath -ErrorAction SilentlyContinue
        if (!$key -or $null -eq $key.NoAutoUpdate -or [int]$key.NoAutoUpdate -ne 1) {
            Save-Status 'ChangedExternally' 'Настройка изменилась извне; приложение не перезаписывало текущую политику.'
            return
        }
        if ($state.ValueExisted) {
            New-ItemProperty -LiteralPath $policyPath -Name NoAutoUpdate -PropertyType DWord -Value ([int]$state.Value) -Force | Out-Null
        } else {
            Remove-ItemProperty -LiteralPath $policyPath -Name NoAutoUpdate
            $remaining = (Get-ItemProperty -LiteralPath $policyPath).PSObject.Properties |
                Where-Object { $_.Name -notmatch '^PS' }
            if (!$remaining -and !$state.KeyExisted) { Remove-Item -LiteralPath $policyPath -Force }
        }
        Remove-Item -LiteralPath $statePath -Force
        Save-Status 'Restored' 'Исходная настройка автоматических обновлений восстановлена.'
    }
} catch {
    Save-Status 'Failed' ('Не удалось изменить настройку: ' + $_.Exception.Message)
    throw
}
