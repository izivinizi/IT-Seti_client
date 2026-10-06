param([string]$Root = (Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference='Stop'
$tokens=$null; $errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $Root 'src\ITSeti.Maintenance.Infrastructure\Backend\ServerSoftware.ps1'),[ref]$tokens,[ref]$errors)
if($errors){throw ($errors -join '; ')}
$validator=$ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Assert-ServerPackage'},$true)
. ([scriptblock]::Create($validator.Extent.Text))
$normalizer=$ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Normalize-ComponentKey'},$true)
. ([scriptblock]::Create($normalizer.Extent.Text))
$body=@($ast.EndBlock.Statements | Where-Object {$_ -is [Management.Automation.Language.TryStatementAst]})
if($body.Count -ne 1){throw 'Worker body could not be isolated for fixture execution.'}
$worker=[scriptblock]::Create($body[0].Extent.Text)
$fixture=Join-Path $env:TEMP ('ITSeti-software-fixture-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture -Force | Out-Null
$root=$fixture
$CatalogOnly=$false; $AutoUpdate=$false; $Component=' RMS Host '; $locked=$false
$script:installCalls=0; $script:installed=$false
$script:packageBytes=[Text.Encoding]::UTF8.GetBytes('unsigned fixture never executed')
$sha=[Security.Cryptography.SHA256]::Create()
try{$hash=[BitConverter]::ToString($sha.ComputeHash($script:packageBytes)).Replace('-','')}finally{$sha.Dispose()}
$package=@{id=[Guid]::NewGuid().ToString();key='rms';platform='windows';version='7.7.3';fileName='rms.msi';sizeBytes=$script:packageBytes.Length;sha256=$hash}
function New-DownloadRequest([string]$Route){
    $bytes=if($Route -eq '/api/v1/updates'){[Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject @($script:package) -Compress))}else{$script:packageBytes}
    $response=New-Object PSObject -Property @{Buffer=$bytes}
    $response | Add-Member ScriptMethod GetResponseStream {return New-Object IO.MemoryStream -ArgumentList (,[byte[]]$this.Buffer)}
    $response | Add-Member ScriptMethod Dispose {}
    $request=New-Object PSObject -Property @{Response=$response}
    $request | Add-Member ScriptMethod GetResponse {return $this.Response}
    return $request
}
function Find-InstalledFile([string]$Key){if($script:installed){return (Join-Path $fixture 'installed.marker')}}
function Start-Process {
    param($FilePath,$ArgumentList,$WorkingDirectory,$WindowStyle,[switch]$PassThru)
    if($WindowStyle -ne 'Hidden' -or $ArgumentList -notmatch '/qn.*REBOOT=ReallySuppress'){throw 'Installer was not silent.'}
    $script:installCalls++; $script:installed=$true
    [IO.File]::WriteAllText((Join-Path $fixture 'installed.marker'),'fixture')
    $process=New-Object PSObject -Property @{ExitCode=0;Id=0}
    $process | Add-Member ScriptMethod WaitForExit {param($timeout) return $true}
    $process | Add-Member ScriptMethod Dispose {}
    return $process
}
function New-FixtureMutex {
    $value=New-Object PSObject
    $value | Add-Member ScriptMethod WaitOne {param($timeout) return $true}
    $value | Add-Member ScriptMethod ReleaseMutex {}
    $value | Add-Member ScriptMethod Dispose {}
    return $value
}
try {
    if ((Normalize-ComponentKey ' OCS Inventory ') -ne 'ocs') { throw 'OCS display name was not normalized.' }
    if ((Normalize-ComponentKey 'RMS Host') -ne 'rms') { throw 'RMS display name was not normalized.' }
    $script:package=$package
    $mutex=New-FixtureMutex
    & $worker
    if($script:installCalls -ne 1){throw 'Verified manual package did not reach the mocked installer.'}
    $script:installed=$false; $script:installCalls=0
    $AutoUpdate=$true; $mutex=New-FixtureMutex
    & $worker
    if($script:installCalls -ne 0){throw 'Auto update installed absent software.'}
    $AutoUpdate=$false; $package.sha256='b'*64; $mutex=New-FixtureMutex
    $rejected=$false
    try{& $worker}catch{$rejected=$true}
    if(!$rejected -or $script:installCalls -ne 0){throw 'Corrupted package reached the installer.'}
    if(Get-ChildItem -LiteralPath (Join-Path $fixture 'ServerPackages') -Filter '*.pending'){throw 'Partial package survived rejection.'}
    'PASS: runtime fixture downloads, verifies and installs through a mock; corrupt packages rejected and missing software never auto-installed. No system software changed.'
} finally {
    if([IO.Path]::GetFullPath($fixture).StartsWith([IO.Path]::GetFullPath($env:TEMP).TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){
        Remove-Item -LiteralPath $fixture -Recurse -Force
    }
}
