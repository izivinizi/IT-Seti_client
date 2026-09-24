param([ValidateSet('Choose','AppOnly','Combined')][string]$Mode='Choose')
$ErrorActionPreference='Stop'
$appUninstaller=Join-Path $PSScriptRoot 'unins000.exe'
$softwareUninstaller=Join-Path $PSScriptRoot 'OrganizationUninstall.ps1'

function Show-Choice {
    Add-Type -AssemblyName System.Windows.Forms,System.Drawing
    [Windows.Forms.Application]::EnableVisualStyles()
    $form=New-Object Windows.Forms.Form -Property @{
        Text='Удаление ИТ-Сети';Width=490;Height=290;StartPosition='CenterScreen'
        FormBorderStyle='FixedDialog';MaximizeBox=$false;MinimizeBox=$false
        Font=(New-Object Drawing.Font('Segoe UI',10));BackColor=[Drawing.Color]::White
    }
    $title=New-Object Windows.Forms.Label -Property @{Text='Что удалить?';Left=24;Top=22;Width=420;Height=32;Font=(New-Object Drawing.Font('Segoe UI',16,[Drawing.FontStyle]::Bold))}
    $app=New-Object Windows.Forms.RadioButton -Property @{Text='Только приложение обслуживания ПК';Left=26;Top=70;Width=420;Height=28;Checked=$true}
    $both=New-Object Windows.Forms.RadioButton -Property @{Text='Приложение и программы ИТ-Сети';Left=26;Top=111;Width=420;Height=28}
    $note=New-Object Windows.Forms.Label -Property @{
        Text='Во втором варианте вы выберете найденные AnyDesk, RMS, OCS и панель. Могут удалиться и программы, установленные отдельно. ID сохранятся.'
        Left=47;Top=140;Width=395;Height=46;ForeColor=[Drawing.Color]::DimGray
    }
    $cancel=New-Object Windows.Forms.Button -Property @{Text='Отмена';Left=288;Top=210;Width=78;Height=32;DialogResult='Cancel'}
    $remove=New-Object Windows.Forms.Button -Property @{Text='Продолжить';Left=371;Top=210;Width=92;Height=32;DialogResult='OK'}
    $form.Controls.AddRange(@($title,$app,$both,$note,$cancel,$remove))
    $form.AcceptButton=$remove;$form.CancelButton=$cancel
    try {
        if($form.ShowDialog() -ne [Windows.Forms.DialogResult]::OK){return $null}
        return $(if($both.Checked){'Combined'}else{'AppOnly'})
    } finally {$form.Dispose()}
}

try {
    if($Mode -eq 'Choose'){$Mode=Show-Choice;if(!$Mode){exit 0}}
    if(!(Test-Path -LiteralPath $appUninstaller -PathType Leaf)){throw 'Не найден unins000.exe в папке приложения.'}
    if($Mode -eq 'Combined'){
        if(!(Test-Path -LiteralPath $softwareUninstaller -PathType Leaf)){throw 'Не найден OrganizationUninstall.ps1 в папке приложения.'}
        $admin=([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
        if(!$admin){
            $path=$PSCommandPath.Replace("'","''")
            $command="& '$path' -Mode Combined"
            $encoded=[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
            $elevated=Start-Process -FilePath (Join-Path $PSHOME 'powershell.exe') -Verb RunAs -WindowStyle Hidden -ArgumentList "-NoProfile -ExecutionPolicy Bypass -EncodedCommand $encoded" -PassThru -Wait
            exit $elevated.ExitCode
        }
        $software=Start-Process -FilePath (Join-Path $PSHOME 'powershell.exe') -WindowStyle Hidden -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',('"'+$softwareUninstaller+'"')) -PassThru -Wait
        if($software.ExitCode -eq 3){exit 0}
        if($software.ExitCode -ne 0){throw 'Удаление программ ИТ-Сети завершилось с ошибками. Приложение оставлено; проверьте журнал в ProgramData\ITSETI-uninstall.log.'}
    }
    $app=Start-Process -FilePath $appUninstaller -PassThru -Wait
    exit $app.ExitCode
} catch {
    Add-Type -AssemblyName System.Windows.Forms
    [Windows.Forms.MessageBox]::Show($_.Exception.Message,'Удаление ИТ-Сети','OK','Warning') | Out-Null
    exit 2
}
