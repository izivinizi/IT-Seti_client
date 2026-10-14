param([string]$Root=(Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference='Stop'
$ScriptRoot=Join-Path $env:TEMP ('ITSeti-DiskSpdProcess-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $ScriptRoot | Out-Null
$worker=Join-Path $Root 'src\ITSeti.Maintenance.Infrastructure\Backend\HeadlessDiskWorker.ps1'
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile([IO.Path]::GetFullPath($worker),[ref]$tokens,[ref]$errors)
if($errors){throw ($errors | Out-String)}
foreach($definition in $ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst]},$true)) {
    . ([scriptblock]::Create($definition.Extent.Text))
}
$fixture=Join-Path $ScriptRoot 'FastExit.exe'
$target=Join-Path $ScriptRoot 'unused.dat'
try {
    # Exits immediately, without disk I/O; real process handles and redirected output are exercised.
    $source=Join-Path $ScriptRoot 'FastExit.cs'
    [IO.File]::WriteAllText($source,@'
using System;
public class FastExit {
    public static int Main() {
        Console.WriteLine("<Results><TimeSpan><TestTimeSeconds>1</TestTimeSeconds><Thread><Target><ReadBytes>100000000</ReadBytes><WriteBytes>100000000</WriteBytes></Target></Thread></TimeSpan></Results>");
        string failure = Environment.GetEnvironmentVariable("ITSETI_PROCESS_TEST_FAILURE");
        if (failure == "1") { Console.Error.WriteLine("ERROR: fixture failure"); return 7; }
        return 0;
    }
}
'@,[Text.Encoding]::ASCII)
    $compilerCandidates=@(
        (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
        (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
    )
    $compiler=$compilerCandidates | Where-Object {Test-Path -LiteralPath $_ -PathType Leaf} | Select-Object -First 1
    if($compiler) {
        $compile=Start-Process -FilePath $compiler -ArgumentList ('/nologo /target:exe /out:"'+$fixture+'" "'+$source+'"') -Wait -PassThru
        if($compile.ExitCode -ne 0 -or !(Test-Path -LiteralPath $fixture)){throw 'Fast process fixture did not compile.'}
    } else {
        try {Add-Type -TypeDefinition ([IO.File]::ReadAllText($source)) -OutputAssembly $fixture -OutputType ConsoleApplication -ErrorAction Stop}
        catch {throw 'A console C# compiler is required for this process-handle test: '+$_.Exception.Message}
    }
    if((Get-UnsignedCounterDelta32 4294967290 5) -ne 11){throw '32-bit PDH counter wrap was not handled.'}
    $before=[pscustomobject]@{Frequency_Sys100NS=10000000;AvgDisksecPerRead=100000000;AvgDisksecPerRead_Base=1000}
    $after=[pscustomobject]@{Frequency_Sys100NS=10000000;AvgDisksecPerRead=200000000;AvgDisksecPerRead_Base=2000}
    $measured=Measure-DiskLatencyDelta $before $after 'Read'
    if(!$measured -or [math]::Abs($measured.LatencyMs-10) -gt 0.001 -or $measured.Operations -ne 1000){throw 'PDH average disk latency conversion is incorrect.'}
    $changedFrequency=[pscustomobject]@{Frequency_Sys100NS=20000000;AvgDisksecPerRead=200000000;AvgDisksecPerRead_Base=2000}
    if(Measure-DiskLatencyDelta $before $changedFrequency 'Read') {throw 'Changed performance-counter frequency was accepted.'}
    for($i=0;$i -lt 12;$i++) {
        if((Invoke-DiskSpd $fixture 'read' 1) -ne 100){throw 'Fast process result lost'}
    }
    $script:counterSamples=@(
        [pscustomobject]@{Frequency_Sys100NS=10000000;AvgDisksecPerRead=100000000;AvgDisksecPerRead_Base=1000},
        [pscustomobject]@{Frequency_Sys100NS=10000000;AvgDisksecPerRead=200000000;AvgDisksecPerRead_Base=2000},
        [pscustomobject]@{Frequency_Sys100NS=10000000;AvgDisksecPerWrite=100000000;AvgDisksecPerWrite_Base=20},
        [pscustomobject]@{Frequency_Sys100NS=10000000;AvgDisksecPerWrite=250000000;AvgDisksecPerWrite_Base=1020}
    )
    $script:counterIndex=0
    function Get-SystemDrivePerfCounter {
        $sample=$script:counterSamples[$script:counterIndex]
        $script:counterIndex++
        return $sample
    }
    $script:DiskReadLatencyWeighted=0.0;$script:DiskWriteLatencyWeighted=0.0
    $script:DiskReadOperations=[long]0;$script:DiskWriteOperations=[long]0
    $script:DiskLatencyIssues=@()
    if((Invoke-DiskSpd $fixture 'read' 2) -ne 100 -or (Invoke-DiskSpd $fixture 'write' 1) -ne 100){throw 'Measured DiskSpd fixture result lost'}
    if($script:counterIndex -ne 4 -or $script:DiskReadOperations -ne 1000 -or $script:DiskWriteOperations -ne 1000 -or
        [math]::Abs(($script:DiskReadLatencyWeighted/$script:DiskReadOperations)-10) -gt 0.001 -or
        [math]::Abs(($script:DiskWriteLatencyWeighted/$script:DiskWriteOperations)-15) -gt 0.001) {
        throw 'DiskSpd read/write latency was not measured and accumulated from counters.'
    }
    $env:ITSETI_PROCESS_TEST_FAILURE='1'
    $failure=$null
    try { Invoke-DiskSpd $fixture 'read' 1 | Out-Null } catch {$failure=$_.Exception.Message}
    if($failure -notmatch 'code 7' -or $failure -notmatch 'ERROR: fixture failure'){throw 'Nonzero exit or stderr lost'}
    'PASS: DiskSpd process exits, XML, stderr, 32-bit PDH wrap and read/write latency math'
} finally {
    Remove-Item Env:\ITSETI_PROCESS_TEST_FAILURE -ErrorAction SilentlyContinue
    foreach($file in Get-ChildItem -LiteralPath $ScriptRoot -File){Remove-Item -LiteralPath $file.FullName -Force}
    [IO.Directory]::Delete($ScriptRoot)
}
