param([string]$Root = (Split-Path $PSScriptRoot -Parent), [string]$Output)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[Windows.Forms.Application]::EnableVisualStyles()
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $Root 'Connect-Server.ps1'), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw ($errors -join [Environment]::NewLine) }
$script:existing = $false
$statements = @($ast.EndBlock.Statements)
$inside = $false
foreach ($statement in $statements) {
    $source = $statement.Extent.Text
    if ($source -match '^\$form = New-Object Windows.Forms.Form') { $inside = $true }
    if ($source -match '^\$script:companies =') { break }
    if ($inside) { Invoke-Expression $source }
}
try {
    if ($form.ClientSize.Height -gt 360) { throw 'Assignment dialog is unnecessarily tall.' }
    if ($companySearch.Top -ne $companyBox.Top -or $siteSearch.Top -ne $siteBox.Top) { throw 'Search and list must share a row.' }
    foreach ($control in $form.Controls) {
        if ($control.Right -gt $form.ClientSize.Width -or $control.Bottom -gt $form.ClientSize.Height) { throw ('Clipped control: ' + $control.Text) }
    }
    $companySearch.Enabled = $companyBox.Enabled = $siteSearch.Enabled = $siteBox.Enabled = $true
    [void]$companyBox.Items.Add('IT-Seti Group')
    $companyBox.SelectedIndex = 0
    [void]$siteBox.Items.Add('No site')
    $siteBox.SelectedIndex = 0
    if ($Output) {
        $form.Show()
        [Windows.Forms.Application]::DoEvents()
        $bitmap = New-Object Drawing.Bitmap($form.Width, $form.Height)
        try {
            $form.DrawToBitmap($bitmap, (New-Object Drawing.Rectangle(0, 0, $form.Width, $form.Height)))
            $bitmap.Save($Output, [Drawing.Imaging.ImageFormat]::Png)
        } finally { $bitmap.Dispose() }
    }
    Write-Output 'PASS: compact assignment dialog, aligned searches and no clipped controls.'
} finally { $form.Dispose() }
