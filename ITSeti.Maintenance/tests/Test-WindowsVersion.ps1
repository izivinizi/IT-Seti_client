param([string]$Root=(Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference='Stop'
. (Join-Path $Root 'src/ITSeti.Maintenance.Infrastructure/Backend/Summary.ps1')
$key=[pscustomobject]@{ProductName='Windows 10 Pro';DisplayVersion='25H2';ReleaseId='2009';CurrentBuildNumber='19045';CurrentBuild='19045'}
$cases=@(
    @{Caption='Microsoft Windows 11 Pro';BuildNumber='26200';ProductType=1;Expected='Microsoft Windows 11 Pro'},
    @{Caption='Windows 10 Pro';BuildNumber='26200';ProductType=1;Expected='Windows 11 Pro'},
    @{Caption='Windows 10 Pro';BuildNumber='19045';ProductType=1;Expected='Windows 10 Pro'},
    @{Caption='Windows Server 2025';BuildNumber='26100';ProductType=3;Expected='Windows Server 2025'},
    @{Caption='Windows 7 Professional';BuildNumber='7601';ProductType=1;Expected='Windows 7 Professional'}
)
foreach($case in $cases){
    $result=Get-WindowsVersionDetails ([pscustomobject]$case) $key
    if($result.Edition -ne $case.Expected -or $result.Build -ne [int]$case.BuildNumber -or $result.Release -ne '25H2'){
        throw 'Windows identity or authoritative release was altered incorrectly.'
    }
}
$os=Get-CimInstance Win32_OperatingSystem
$registry=Get-ItemProperty 'HKLM:/SOFTWARE/Microsoft/Windows NT/CurrentVersion'
$actual=Get-WindowsVersionDetails $os $registry
if(!$actual.Edition -or !$actual.Build){throw 'Actual OS identity missing.'}
Write-Host ('PASS backend Windows identity: '+$actual.Edition+' | '+$actual.Release+' | '+$actual.Build)
