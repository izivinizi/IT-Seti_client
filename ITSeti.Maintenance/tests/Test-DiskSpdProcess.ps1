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
    Add-Type -TypeDefinition @'
using System;
public class FastExit {
    public static int Main() {
        Console.WriteLine("<Results><TimeSpan><TestTimeSeconds>1</TestTimeSeconds><Thread><Target><ReadBytes>100000000</ReadBytes></Target></Thread></TimeSpan></Results>");
        string failure = Environment.GetEnvironmentVariable("ITSETI_PROCESS_TEST_FAILURE");
        if (failure == "1") { Console.Error.WriteLine("ERROR: fixture failure"); return 7; }
        return 0;
    }
}
'@ -OutputAssembly $fixture -OutputType ConsoleApplication
    for($i=0;$i -lt 12;$i++) {
        if((Invoke-DiskSpd $fixture 'read' 1) -ne 100){throw 'Fast process result lost'}
    }
    $env:ITSETI_PROCESS_TEST_FAILURE='1'
    $failure=$null
    try { Invoke-DiskSpd $fixture 'read' 1 | Out-Null } catch {$failure=$_.Exception.Message}
    if($failure -notmatch 'code 7' -or $failure -notmatch 'ERROR: fixture failure'){throw 'Nonzero exit or stderr lost'}
    'PASS: 12 fast process exits preserve ExitCode and XML; exit 7 preserves error details'
} finally {
    Remove-Item Env:\ITSETI_PROCESS_TEST_FAILURE -ErrorAction SilentlyContinue
    foreach($file in Get-ChildItem -LiteralPath $ScriptRoot -File){Remove-Item -LiteralPath $file.FullName -Force}
    [IO.Directory]::Delete($ScriptRoot)
}
