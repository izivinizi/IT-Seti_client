param([string]$JobRoot,[string]$SignalRoot=$JobRoot,[switch]$TestOnly)
$ErrorActionPreference='Stop'
function Status([string]$Text) { $Text | Set-Content -LiteralPath (Join-Path $JobRoot 'user-status.txt') -Encoding UTF8 }
function Clear-ProfileDirectory([string]$Path,[datetime]$OlderThanUtc=[datetime]::MaxValue) {
    $profile=[IO.Path]::GetFullPath($env:USERPROFILE).TrimEnd('\')+'\'
    $root=[IO.Path]::GetFullPath($Path).TrimEnd('\')
    if(!$root.StartsWith($profile,[StringComparison]::OrdinalIgnoreCase)){throw 'Путь очистки вышел за пределы профиля пользователя.'}
    if(!(Test-Path -LiteralPath $root -PathType Container)){return}
    if(([IO.File]::GetAttributes($root) -band [IO.FileAttributes]::ReparsePoint) -ne 0){return}
    $pending=New-Object 'System.Collections.Generic.Stack[string]'
    $directories=New-Object 'System.Collections.Generic.List[string]'
    $pending.Push($root)
    while($pending.Count -gt 0) {
        $directory=$pending.Pop()
        try {$entries=[IO.Directory]::GetFileSystemEntries($directory)} catch [IO.IOException] {continue} catch [UnauthorizedAccessException] {continue}
        foreach($entry in $entries) {
            try {
                $attributes=[IO.File]::GetAttributes($entry)
                if(($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0){continue}
                if(($attributes -band [IO.FileAttributes]::Directory) -ne 0) {
                    $directories.Add($entry); $pending.Push($entry); continue
                }
                if([IO.File]::GetLastWriteTimeUtc($entry) -ge $OlderThanUtc){continue}
                [IO.File]::Delete($entry)
            } catch [IO.IOException] {} catch [UnauthorizedAccessException] {}
        }
    }
    foreach($directory in ($directories | Sort-Object Length -Descending)) {
        try { if(![IO.Directory]::GetFileSystemEntries($directory).Length){[IO.Directory]::Delete($directory,$false)} }
        catch [IO.IOException] {} catch [UnauthorizedAccessException] {}
    }
}
function Clear-ThumbnailCache {
    $path=Join-Path $env:LOCALAPPDATA 'Microsoft\Windows\Explorer'
    if(!(Test-Path -LiteralPath $path -PathType Container)){return}
    if(([IO.File]::GetAttributes($path) -band [IO.FileAttributes]::ReparsePoint) -ne 0){return}
    foreach($pattern in @('thumbcache_*.db','iconcache_*.db')) {
        foreach($file in [IO.Directory]::GetFiles($path,$pattern,[IO.SearchOption]::TopDirectoryOnly)) {
            try {
                if(([IO.File]::GetAttributes($file) -band [IO.FileAttributes]::ReparsePoint) -eq 0){[IO.File]::Delete($file)}
            } catch [IO.IOException] {} catch [UnauthorizedAccessException] {}
        }
    }
}
function Clear-CurrentUserRecycleBin {
    if(!('ITSetiUserRecycleBin' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class ITSetiUserRecycleBin {
    [DllImport("shell32.dll", CharSet=CharSet.Unicode)]
    public static extern int SHEmptyRecycleBin(IntPtr hwnd, string rootPath, uint flags);
}
'@
    }
    $result=[ITSetiUserRecycleBin]::SHEmptyRecycleBin([IntPtr]::Zero,$null,7)
    if($result -lt 0){[Runtime.InteropServices.Marshal]::ThrowExceptionForHR($result)}
}
try {
    [Diagnostics.Process]::GetCurrentProcess().PriorityClass='BelowNormal'
    $identity=[Security.Principal.WindowsIdentity]::GetCurrent().Name
    Status ("Ожидает тестов | пользователь: "+$identity)
    'ready' | Set-Content -LiteralPath (Join-Path $JobRoot 'ready.txt')
    $signal=Join-Path $SignalRoot 'go.txt'
    $deadline=(Get-Date).AddHours(1)
    $localSignal=Join-Path $JobRoot 'go.txt'
    while(!(Test-Path -LiteralPath $signal) -and !(Test-Path -LiteralPath $localSignal)) {
        if((Get-Date) -gt $deadline){throw 'Запуск не подтверждён за час. Очистка не выполнялась.'}
        Start-Sleep -Seconds 2
    }
    $chosenSignal=if(Test-Path -LiteralPath $signal){$signal}else{$localSignal}
    $value=[IO.File]::ReadAllText($chosenSignal).Trim()
    if($value -eq 'abort'){Status ('Очистка не запускалась | пользователь: '+$identity); return}
    if($value -notmatch '^\d{4}$' -and $value -ne 'fallback'){throw 'Некорректный режим очистки.'}
    Status ("Очистка профиля "+$identity)
    if(!$TestOnly) {
        if($value -eq 'fallback') {
            Clear-ProfileDirectory $env:TEMP ([DateTime]::UtcNow.AddDays(-2))
            Clear-ThumbnailCache
            Clear-ProfileDirectory (Join-Path $env:LOCALAPPDATA 'Microsoft\Windows\Temporary Internet Files')
            Clear-ProfileDirectory (Join-Path $env:LOCALAPPDATA 'Microsoft\Windows\INetCache')
            Clear-ProfileDirectory (Join-Path $env:LOCALAPPDATA 'D3DSCache')
            Clear-CurrentUserRecycleBin
        } else {
            $start=New-Object Diagnostics.ProcessStartInfo
            $start.FileName=Join-Path $env:windir 'System32\cleanmgr.exe'
            $start.Arguments='/sagerun:'+([int]$value)
            $start.UseShellExecute=$false
            $start.CreateNoWindow=$false
            $p=[Diagnostics.Process]::Start($start)
            $p.WaitForExit()
            if($p.ExitCode -ne 0){throw ('cleanmgr: код '+$p.ExitCode)}
        }
    }
    Status ("Завершена | пользователь: "+$identity+$(if($value -eq 'fallback'){' | базовая очистка профиля'}else{''})+$(if($TestOnly){' | TEST OK'}))
} catch { Status ('Не выполнена: '+$_.Exception.Message) }
finally { 'done' | Set-Content -LiteralPath (Join-Path $JobRoot 'done.txt') }
