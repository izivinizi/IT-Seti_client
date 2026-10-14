param([string]$Root=(Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference='Stop'
$testId=[guid]::NewGuid().ToString('N')
$registryPath='HKCU:\Software\ITSeti\DiagnosticDataPolicyTest\'+$testId
$temporaryRoot=Join-Path ([IO.Path]::GetTempPath()) 'ITSeti-PolicyTests'
$testRoot=Join-Path $temporaryRoot ('diagnostic-data-test-'+$testId)
$originalProgramData=$env:ProgramData
try {
    New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
    $source=Join-Path $Root 'src\ITSeti.Maintenance.Infrastructure\Backend\Set-WindowsDiagnosticData.ps1'
    $content=[IO.File]::ReadAllText($source,[Text.Encoding]::UTF8)
    $content=$content.Replace('HKLM:\SOFTWARE\Policies\Microsoft\Windows\DataCollection',$registryPath)
    $content=$content.Replace("(Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' -Name EditionID -ErrorAction Stop)","([pscustomobject]@{EditionID='Professional'})")
    $testScript=Join-Path $testRoot 'policy.ps1'
    [IO.File]::WriteAllText($testScript,$content,[Text.UTF8Encoding]::new($true))
    $env:ProgramData=$testRoot
    New-Item -Path $registryPath -Force | Out-Null
    New-ItemProperty -LiteralPath $registryPath -Name AllowTelemetry -PropertyType DWord -Value 3 -Force | Out-Null
    & $testScript -Action Minimize
    if([int](Get-ItemProperty -LiteralPath $registryPath).AllowTelemetry -ne 1){throw 'Pro edition did not use minimum supported level 1.'}
    & $testScript -Action Restore
    if([int](Get-ItemProperty -LiteralPath $registryPath).AllowTelemetry -ne 3){throw 'Original diagnostic data level was not restored.'}
    Remove-ItemProperty -LiteralPath $registryPath -Name AllowTelemetry
    & $testScript -Action Minimize
    & $testScript -Action Restore
    if($null -ne (Get-ItemProperty -LiteralPath $registryPath).AllowTelemetry){throw 'Absent policy value was not restored.'}
    $enterpriseScript=Join-Path $testRoot 'enterprise-policy.ps1'
    [IO.File]::WriteAllText($enterpriseScript,$content.Replace("EditionID='Professional'","EditionID='Enterprise'"),[Text.UTF8Encoding]::new($true))
    & $enterpriseScript -Action Minimize
    if([int](Get-ItemProperty -LiteralPath $registryPath).AllowTelemetry -ne 0){throw 'Enterprise edition did not use diagnostic data level 0.'}
    & $enterpriseScript -Action Restore
    if($null -ne (Get-ItemProperty -LiteralPath $registryPath).AllowTelemetry){throw 'Enterprise policy value was not restored.'}
    Write-Host 'PASS: Pro level 1, Enterprise level 0, and original or absent values restore in isolated HKCU key.'
} finally {
    $env:ProgramData=$originalProgramData
    Remove-Item -LiteralPath $registryPath -Recurse -Force -ErrorAction SilentlyContinue
    $resolvedRoot=[IO.Path]::GetFullPath($temporaryRoot).TrimEnd('\')+'\'
    $resolvedTest=[IO.Path]::GetFullPath($testRoot)
    if($resolvedTest.StartsWith($resolvedRoot,[StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $resolvedTest -Recurse -Force -ErrorAction SilentlyContinue
    }
}
