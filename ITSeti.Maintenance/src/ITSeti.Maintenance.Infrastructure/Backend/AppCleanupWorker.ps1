param([string]$JobRoot,[string]$TrustedScriptsRoot=$PSScriptRoot,[string]$StatusRoot=$JobRoot)
$ErrorActionPreference='Stop'
$script:Admin=([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$script:CompactOutput=$true
$OriginalIdentity=(Get-Acl -LiteralPath $JobRoot).Owner
function Write-Stage([string]$Text) {$Text | Set-Content -LiteralPath (Join-Path $StatusRoot 'admin-stage.txt') -Encoding UTF8}
function Section([string]$Text) {Write-Stage $Text}
try {
    if(!$script:Admin){throw 'Повышенные права не получены.'}
    if(Get-Process cleanmgr,dism,sfc -ErrorAction SilentlyContinue){throw 'Уже работает очистка или восстановление Windows.'}
    $ScriptRoot=$TrustedScriptsRoot
    . ([scriptblock]::Create([IO.File]::ReadAllText((Join-Path $TrustedScriptsRoot 'Cleanup.ps1'),[Text.Encoding]::UTF8)))
    . ([scriptblock]::Create([IO.File]::ReadAllText((Join-Path $TrustedScriptsRoot 'UserCleanup.ps1'),[Text.Encoding]::UTF8)))
    Write-Stage 'Очистка профиля пользователя…'
    try {
        $userResult=Invoke-OriginalUserCleanup $JobRoot $StatusRoot
        $userResult | Set-Content -LiteralPath (Join-Path $StatusRoot 'user-cleanup.txt') -Encoding UTF8
    } catch {('Не выполнена: '+$_.Exception.Message) | Set-Content -LiteralPath (Join-Path $StatusRoot 'user-cleanup.txt') -Encoding UTF8}
    Write-Stage 'Очистка системных файлов и обновлений Windows…'
    try {
        Invoke-Cleanup
        ('Код {0}; изменение свободного места {1:N2} ГБ (включая работу других программ).' -f $script:CleanupResult.ExitCode,$script:CleanupResult.ChangeGB) | Set-Content -LiteralPath (Join-Path $StatusRoot 'admin-cleanup.txt') -Encoding UTF8
    } catch {('Не выполнена: '+$_.Exception.Message) | Set-Content -LiteralPath (Join-Path $StatusRoot 'admin-cleanup.txt') -Encoding UTF8}
    Write-Stage 'Очистка завершена.'
} catch {Write-Stage ('Ошибка очистки: '+$_.Exception.Message)}
