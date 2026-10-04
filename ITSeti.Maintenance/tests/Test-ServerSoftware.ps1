param([string]$Root = (Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference='Stop'
$worker=Join-Path $Root 'src\ITSeti.Maintenance.Infrastructure\Backend\ServerSoftware.ps1'
$tokens=$null; $errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($worker,[ref]$tokens,[ref]$errors)
if($errors){throw ($errors -join '; ')}
$definition=$ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Assert-ServerPackage'},$true)
if(!$definition){throw 'Package validator is missing.'}
. ([scriptblock]::Create($definition.Extent.Text))
$valid=@{id=[Guid]::NewGuid().ToString();key='rms';fileName='rms.msi';sizeBytes=123;sha256=('a'*64)}
Assert-ServerPackage $valid
foreach($case in @(@{key='arbitrary'},@{id=[Guid]::Empty.ToString()},@{sizeBytes=0},@{sizeBytes=2GB},@{sha256='bad'},@{fileName='rms.cmd'},@{fileName='rms.exe'})){
    $bad=$valid.Clone()
    foreach($key in $case.Keys){$bad[$key]=$case[$key]}
    $rejected=$false
    try{Assert-ServerPackage $bad}catch{$rejected=$true}
    if(!$rejected){throw ('Invalid server package accepted: '+($case|ConvertTo-Json -Compress))}
}
$source=[IO.File]::ReadAllText($worker)
foreach($requirement in @('S-1-5-18','AllowAutoRedirect = \$false','Package SHA-256 mismatch','Incomplete package download',
    'Package exceeds declared size','WindowStyle Hidden','REBOOT=ReallySuppress','server-install-','\.unknown',
    'key -notin @\(''winrar'',''yandex''\)','if \(!\$installed\) \{ continue \}')){
    if($source -notmatch $requirement){throw ('Missing software safety contract: '+$requirement)}
}
'PASS: server package metadata validation, SYSTEM gate, bounded download, hashes, silent mode, no auto-install of absent software and install-all exclusions.'
