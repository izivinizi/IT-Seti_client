param([string]$Root = (Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$tokens=$null; $errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $Root 'Connect-Server.ps1'),[ref]$tokens,[ref]$errors)
if($errors){throw ($errors -join '; ')}
$script:existing=$false
$inside=$false
foreach($statement in $ast.EndBlock.Statements){
    $source=$statement.Extent.Text
    if($source -match '^\$form = New-Object Windows.Forms.Form'){$inside=$true}
    if($source -match '^\$loadButton.Add_Click'){break}
    if($inside){Invoke-Expression $source}
}
try {
    $script:companies=@(@{id=1;name='Alpha'},@{id=2;name='Beta'})
    $script:sites=@(@{id=11;companyId=1;name='Office';address='First Street'},@{id=21;companyId=2;name='Warehouse';address='Second Street'})
    $script:catalogLoaded=$true
    $companyBox.Enabled=$true
    Update-CompanyList ''
    $event=[Windows.Forms.ComboBox].GetMethod('OnTextUpdate',[Reflection.BindingFlags]'Instance,NonPublic')
    $companyBox.Text='bet'
    [void]$event.Invoke($companyBox,@([EventArgs]::Empty))
    if($companyBox.Text -ne 'bet' -or $companyBox.Items.Count -ne 1 -or $companyBox.SelectedIndex -ne -1 -or $connectButton.Enabled){throw "Company typing must filter without selecting or submitting: text=$($companyBox.Text); count=$($companyBox.Items.Count); selected=$($companyBox.SelectedIndex); connect=$($connectButton.Enabled)."}
    $companyBox.SelectedIndex=0
    if($script:visibleCompanies[0].id -ne 2 -or $siteBox.Items.Count -ne 2 -or !$siteBox.Enabled){throw 'Selected company must load only its own sites.'}
    $siteBox.Text='second'
    [void]$event.Invoke($siteBox,@([EventArgs]::Empty))
    if($siteBox.Text -ne 'second' -or $siteBox.Items.Count -ne 2 -or $connectButton.Enabled){throw 'Site address search must keep typed text and require a selection.'}
    $siteBox.SelectedIndex=1
    if($script:visibleSites[$siteBox.SelectedIndex].id -ne 21 -or !$connectButton.Enabled){throw 'Filtered site must resolve to the correct catalog ID.'}
    $companyBox.Text='missing'
    [void]$event.Invoke($companyBox,@([EventArgs]::Empty))
    if($companyBox.Items.Count -ne 0 -or $siteBox.Enabled -or $connectButton.Enabled){throw 'Unknown company must invalidate the previous site.'}
    'PASS: inline company/site typing, address filtering, correct IDs and no implicit enrollment selection.'
} finally {$form.Dispose()}
