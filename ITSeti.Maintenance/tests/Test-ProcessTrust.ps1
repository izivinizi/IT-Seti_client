param([string]$Root = (Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference = 'Stop'
$path = Join-Path $Root 'src\ITSeti.Maintenance.Infrastructure\Backend\Summary.ps1'
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($path, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw ($errors -join '; ') }
foreach ($name in @('Test-AllowedPublisher', 'Test-TrustedProcess')) {
    $function = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -eq $name }, $true)
    if (!$function) { throw "Missing process trust rule: $name" }
    . ([scriptblock]::Create($function.Extent.Text))
}
$script:AllowedPublishers = @('Microsoft Corporation', 'Google LLC')
if (!(Test-TrustedProcess 'Valid' 'Google LLC') -or !(Test-TrustedProcess 'Valid' 'Microsoft Corporation')) {
    throw 'A valid signature from a trusted publisher was not recognized.'
}
foreach ($case in @(@('NotSigned', 'Google LLC'), @('UnknownError', 'Microsoft Corporation'),
                   @('Valid', 'Unknown Publisher'), @('NotSigned', ''))) {
    if (Test-TrustedProcess $case[0] $case[1]) {
        throw "Process without a trusted signature was hidden: $($case[0]), $($case[1])"
    }
}
Write-Output 'PASS: process trust requires a valid signature and trusted signer; metadata alone is ignored.'
