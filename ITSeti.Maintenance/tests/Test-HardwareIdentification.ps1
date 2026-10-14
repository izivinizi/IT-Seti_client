param([string]$Root=(Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference='Stop'
$summaryPath=Join-Path $Root 'src\ITSeti.Maintenance.Infrastructure\Backend\Summary.ps1'
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($summaryPath,[ref]$tokens,[ref]$errors)
if($errors){throw ('Summary.ps1 parse error: '+$errors[0].Message)}
foreach($name in @('ConvertTo-MemoryTypeLabel','Get-GpuCandidates')) {
    $definition=@($ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name},$true))
    if($definition.Count -ne 1){throw "Expected one $name helper in Summary.ps1."}
    Invoke-Expression $definition[0].Extent.Text
}
foreach($case in @(
    @{Code=23;Expected='FB-DIMM'},@{Code=25;Expected='FBD2'},@{Code=34;Expected='DDR5'},@{Code=35;Expected='LPDDR5'})) {
    if((ConvertTo-MemoryTypeLabel $case.Code) -ne $case.Expected){throw "Memory code $($case.Code) did not map to $($case.Expected)."}
}
$unknownMemoryType=ConvertTo-MemoryTypeLabel 32767
if(!$unknownMemoryType.EndsWith('32767')){
    throw 'Unknown memory type was dropped instead of preserving its SMBIOS code.'
}
$adapters=@(
    [pscustomobject]@{Name='NVIDIA GeForce Fixture';VideoProcessor='';AdapterCompatibility='';PNPDeviceID='PCI\VEN_10DE&DEV_0001'},
    [pscustomobject]@{Name='Parsec Virtual Display';VideoProcessor='';AdapterCompatibility='';PNPDeviceID='ROOT\PARSEC'},
    [pscustomobject]@{Name='Microsoft Basic Display Adapter';VideoProcessor='';AdapterCompatibility='Advanced Micro Devices';PNPDeviceID='PCI\VEN_1002&DEV_0002'})
$candidates=@(Get-GpuCandidates $adapters)
$physical=@($candidates | Where-Object {!$_.Virtual})
if($physical.Count -ne 2 -or @($physical | Where-Object {!$_.Pci}).Count -ne 0 -or
   @($candidates | Where-Object {$_.Virtual -and $_.Name -like '*Parsec*'}).Count -ne 1) {
    throw 'PCI GPUs were not selected as physical adapters, or a remote display was not classified.'
}
$basic=$physical | Where-Object {$_.Name -eq 'Advanced Micro Devices'}
if(!$basic){throw 'A PCI adapter with the generic Basic Display name did not fall back to its video processor or manufacturer.'}
'PASS: SMBIOS memory types and PCI/virtual GPU identification preserve available hardware details'
