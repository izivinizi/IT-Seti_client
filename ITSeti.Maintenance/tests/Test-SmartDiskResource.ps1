param([string]$Root = (Split-Path $PSScriptRoot -Parent))

$ErrorActionPreference = 'Stop'
$summaryPath = Join-Path $Root 'src\ITSeti.Maintenance.Infrastructure\Backend\Summary.ps1'
$tokens = $null
$errors = $null
$summaryText = [IO.File]::ReadAllText($summaryPath, [Text.Encoding]::UTF8)
$ast = [System.Management.Automation.Language.Parser]::ParseInput($summaryText, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw "Summary.ps1 parse errors: $($errors -join '; ')" }
$function = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'ConvertFrom-CdiReport' }, $true)
if (!$function) { throw 'ConvertFrom-CdiReport was not found.' }
. ([scriptblock]::Create($function.Extent.Text))

$hourReport = @'
Model : Test SSD
Health Status : Good
Drive Letter : C:
Rotation Rate : ---- (SSD)
Interface : NVM Express
Transfer Mode : PCIe 4.0 x4 | PCIe 4.0 x4
Power On Hours : 60,001 hours
'@
$hourDisk = @(ConvertFrom-CdiReport $hourReport)
if ($hourDisk.Count -ne 1 -or $hourDisk[0].PowerOnHours -ne 60001) { throw 'Power On Hours was not parsed as hours.' }

$localizedLabel = -join @([char]0x0427,[char]0x0430,[char]0x0441,[char]0x044B,' ',[char]0x0440,[char]0x0430,[char]0x0431,[char]0x043E,[char]0x0442,[char]0x044B)
$localizedDays = -join @([char]0x0434,[char]0x043D,[char]0x0435,[char]0x0439)
$dayReport = @"
Model : Test HDD
Health Status : Good
Drive Letter : D:
Rotation Rate : 7200 RPM
Interface : Serial ATA
Transfer Mode : SATA/600 | SATA/600
$localizedLabel : 438 $localizedDays
"@
$dayDisk = @(ConvertFrom-CdiReport $dayReport)
if ($dayDisk.Count -ne 1 -or $dayDisk[0].PowerOnHours -ne 10512) { throw 'Localized power-on days were not converted to hours.' }

Write-Output 'PASS: CrystalDiskInfo power-on hours and day conversion.'
