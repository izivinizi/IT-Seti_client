param([string]$Root = (Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference = 'Stop'
$path = Join-Path $Root 'src\ITSeti.Maintenance.Infrastructure\Backend\Update-Application.ps1'
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($path, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw ($errors -join '; ') }
$function = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
    $node.Name -eq 'ConvertTo-AppVersion' }, $true)
if (!$function) { throw 'Application version parser is missing.' }
. ([scriptblock]::Create($function.Extent.Text))
foreach ($text in @('1.2.1', '1.2.1.0', '1.2.1.9')) {
    if ((ConvertTo-AppVersion $text) -ne [version]'1.2.1') { throw "Incorrect version normalization: $text" }
}
foreach ($text in @('1.2', '1.2.1-preview', 'x.y.z')) {
    try { $null = ConvertTo-AppVersion $text; throw "Invalid version accepted: $text" }
    catch { if ($_.Exception.Message -eq "Invalid version accepted: $text") { throw } }
}
Write-Output 'PASS: three- and four-part installed application versions normalize correctly.'
