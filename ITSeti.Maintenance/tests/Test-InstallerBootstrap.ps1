param([string]$Root = (Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference = 'Stop'
$tokens = $null
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $Root 'Install-ITSeti.ps1'), [ref]$tokens, [ref]$parseErrors)
if ($parseErrors) { throw 'Bootstrap syntax is invalid.' }
$credentialTry = @($ast.FindAll({ param($node)
    $node -is [Management.Automation.Language.TryStatementAst] -and
    $node.Body.Statements[0].Extent.Text -eq '$xml = New-Object Xml.XmlDocument'
}, $true))
if ($credentialTry.Count -ne 1) { throw 'Credential launch block not found.' }
$code = [scriptblock]::Create($credentialTry[0].Extent.Text.Replace('$PSScriptRoot', '$fixtureRoot'))
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('itseti-bootstrap-' + [guid]::NewGuid().ToString('N') + '.xml')
try {
    [IO.File]::WriteAllText($fixture, '<credentials><username>Admin</username><password>test-fixture-only</password></credentials>')
    foreach ($scenario in @('success', 'access-denied', 'disabled-service', 'wmi-denied', 'missing-account', 'helper-failure', 'user-account')) {
        & {
            $fixtureRoot = [IO.Path]::GetFullPath($Root)
            $credentialFile = $fixture
            $setup = Join-Path $fixtureRoot 'ITSeti-Maintenance-Setup.exe'
            $script:launches = @()
            function Get-WmiObject {
                param($Class, $Filter, $ErrorAction)
                if ($scenario -eq 'wmi-denied') { throw 'WMI access denied' }
                $account = [pscustomobject]@{ Name = $(if($scenario -eq 'user-account'){'user'}else{'aDmIn'}); SID = 'S-1-5-21-1-2-3-1001'; Disabled = $false; Lockout = $false }
                if ($Class -eq 'Win32_UserAccount') {
                    if ($scenario -ne 'missing-account') { return $account }
                    return
                }
                $group = [pscustomobject]@{ Account = $account }
                $group | Add-Member ScriptMethod GetRelated { param($Class) $this.Account }
                return $group
            }
            function Get-Service {
                param($Name, $ErrorAction)
                [pscustomobject]@{ StartType = $(if($scenario -eq 'disabled-service'){'Disabled'}else{'Manual'}) }
            }
            function Start-Process {
                param($FilePath, $Credential, $ArgumentList, $WorkingDirectory, $WindowStyle, [switch]$Wait, [switch]$PassThru, $ErrorAction, $Verb)
                $script:launches += [pscustomobject]@{ File=$FilePath; User=$Credential.UserName; Verb=$Verb; Arguments=$ArgumentList }
                if ($Credential -and $scenario -eq 'access-denied') { throw 'Access denied' }
                [pscustomobject]@{ ExitCode = $(if($Credential -and $scenario -eq 'helper-failure'){9001}else{0}) }
            }
            function Start-AdministratorHelper {
                param($PowerShell,$Helper,$Credential)
                Start-Process -FilePath $PowerShell -Credential $Credential -ArgumentList $Helper
            }
            & $code
            $last = $script:launches[-1]
            if ($scenario -in 'success','user-account') {
                if ($last.Verb -or $last.File -notlike '*powershell.exe' -or $last.Arguments -notlike '*Install-ITSeti-Admin.ps1*') { throw 'Filtered credentials must launch the elevation helper.' }
                if (!$last.User.StartsWith($env:COMPUTERNAME + '\')) { throw 'Account must be local even on a domain PC.' }
            } elseif ($last.Verb -ne 'RunAs' -or $last.File -ne $setup) { throw ('UAC fallback failed: ' + $scenario) }
            Write-Output ('PASS: bootstrap ' + $scenario)
        }
    }
} finally {
    Remove-Item -LiteralPath $fixture -Force -ErrorAction SilentlyContinue
}
