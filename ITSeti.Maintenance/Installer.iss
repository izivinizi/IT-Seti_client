#define PackageRoot "dist\ITSeti-Maintenance"

[Setup]
AppId={{CFA7B53D-16A9-4D77-9D82-EE68369AB185}
AppName=ИТ-Сети Обслуживание ПК
AppVersion=2.1.1
AppPublisher=ИТ-Сети
DefaultDirName={autopf}\ITSeti Maintenance
DefaultGroupName=ИТ-Сети
OutputDir=dist
OutputBaseFilename=ITSeti-Maintenance-Setup
SetupIconFile=src\ITSeti.Maintenance.App\Assets\favicon.ico
PrivilegesRequired=admin
UsePreviousTasks=no
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
DisableWelcomePage=yes
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
UninstallDisplayIcon={app}\ITSeti.Maintenance.exe

[Languages]
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"

[Files]
Source: "{#PackageRoot}\App\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PackageRoot}\Tools\*"; DestDir: "{app}\Tools"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "Install-Maintenance.ps1"; DestDir: "{tmp}\ITSeti-Package"; Flags: ignoreversion deleteafterinstall
Source: "Install-OrganizationSoftware.ps1"; DestDir: "{tmp}\ITSeti-Package"; Flags: ignoreversion deleteafterinstall
Source: "Setup\ITSETI-Setup\*"; DestDir: "{tmp}\ITSeti-Package\Setup\ITSETI-Setup"; Flags: ignoreversion recursesubdirs createallsubdirs deleteafterinstall
Source: "Uninstall-Maintenance.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "Uninstall-Options.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "OrganizationUninstall.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "Connect-Server.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "Uninstall-ITSeti.cmd"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\ИТ-Сети Обслуживание ПК"; Filename: "{app}\ITSeti.Maintenance.exe"
Name: "{autodesktop}\ИТ-Сети Обслуживание ПК"; Filename: "{app}\ITSeti.Maintenance.exe"
Name: "{autoprograms}\Удалить ИТ-Сети"; Filename: "{app}\Uninstall-ITSeti.cmd"; IconFilename: "{app}\ITSeti.Maintenance.exe"
Name: "{autoprograms}\Подключить ПК к серверу ИТ-Сети"; Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-STA -NoProfile -ExecutionPolicy Bypass -File ""{app}\Connect-Server.ps1"""; IconFilename: "{app}\ITSeti.Maintenance.exe"

[Run]
Filename: "{app}\ITSeti.Maintenance.exe"; Description: "Запустить ИТ-Сети Обслуживание ПК"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\Uninstall-Maintenance.ps1"""; Flags: runhidden waituntilterminated; RunOnceId: "ITSetiMaintenanceCleanup"

[Code]
var
  OptionsPage: TWizardPage;
  InstallDirEdit: TNewEdit;
  InstallDirBrowse: TNewButton;
  SoftwareCheck: TNewCheckBox;
  InventoryEdit: TNewEdit;

procedure BrowseInstallDir(Sender: TObject);
var
  Path: String;
begin
  Path := InstallDirEdit.Text;
  if BrowseForFolder('Папка установки приложения', Path, True) then InstallDirEdit.Text := Path;
end;

procedure AddLabel(const Caption: String; Top: Integer);
var
  LabelControl: TNewStaticText;
begin
  LabelControl := TNewStaticText.Create(OptionsPage);
  LabelControl.Parent := OptionsPage.Surface;
  LabelControl.Caption := Caption;
  LabelControl.Left := 0;
  LabelControl.Top := Top;
  LabelControl.Width := OptionsPage.SurfaceWidth;
  LabelControl.Font.Size := 10;
end;

procedure InitializeWizard;
var
  Existing: AnsiString;
begin
  OptionsPage := CreateCustomPage(wpWelcome, 'Настройка обслуживания ПК',
    'Укажите параметры установки и нажмите Далее.');
  AddLabel('Папка приложения', 0);
  InstallDirEdit := TNewEdit.Create(OptionsPage);
  InstallDirEdit.Parent := OptionsPage.Surface;
  InstallDirEdit.Left := 0;
  InstallDirEdit.Top := 22;
  InstallDirEdit.Width := OptionsPage.SurfaceWidth - 90;
  InstallDirEdit.Text := WizardForm.DirEdit.Text;
  InstallDirBrowse := TNewButton.Create(OptionsPage);
  InstallDirBrowse.Parent := OptionsPage.Surface;
  InstallDirBrowse.Left := InstallDirEdit.Width + 8;
  InstallDirBrowse.Top := 20;
  InstallDirBrowse.Width := 82;
  InstallDirBrowse.Height := 27;
  InstallDirBrowse.Caption := 'Обзор...';
  InstallDirBrowse.OnClick := @BrowseInstallDir;

  SoftwareCheck := TNewCheckBox.Create(OptionsPage);
  SoftwareCheck.Parent := OptionsPage.Surface;
  SoftwareCheck.Left := 0;
  SoftwareCheck.Top := 68;
  SoftwareCheck.Width := OptionsPage.SurfaceWidth;
  SoftwareCheck.Height := 27;
  SoftwareCheck.Caption := 'Установить AnyDesk, RMS, OCS и панель ИТ-Сети';
  SoftwareCheck.Font.Size := 10;

  AddLabel('Установочные файлы программ ИТ-Сети включены в приложение.', 106);
  AddLabel('Инвентарный номер (четыре цифры, необязательно)', 145);
  InventoryEdit := TNewEdit.Create(OptionsPage);
  InventoryEdit.Parent := OptionsPage.Surface;
  InventoryEdit.Left := 0;
  InventoryEdit.Top := 167;
  InventoryEdit.Width := 120;
  InventoryEdit.MaxLength := 4;
  if LoadStringFromFile(ExpandConstant('{commonappdata}\ITSeti\Maintenance\inventory.txt'), Existing) then
    InventoryEdit.Text := Trim(Existing);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Value: String;
  I: Integer;
begin
  Result := True;
  if CurPageID <> OptionsPage.ID then Exit;
  if Trim(InstallDirEdit.Text) = '' then
  begin
    MsgBox('Укажите папку приложения.', mbError, MB_OK);
    Result := False;
    Exit;
  end;
  WizardForm.DirEdit.Text := Trim(InstallDirEdit.Text);
  Value := Trim(InventoryEdit.Text);
  if Value = '' then Exit;
  if Length(Value) <> 4 then Result := False;
  if Result then
    for I := 1 to Length(Value) do
      if (Value[I] < '0') or (Value[I] > '9') then Result := False;
  if not Result then MsgBox('Введите ровно четыре цифры.', mbError, MB_OK);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Code: Integer;
  SoftwareCode: Integer;
  ResultPath: String;
  ResultText: AnsiString;
  MaintenanceResult: String;
  MaintenanceText: AnsiString;
  ConnectCode: Integer;
begin
  if CurStep = ssInstall then
  begin
    if Trim(InventoryEdit.Text) <> '' then
    begin
      if not ForceDirectories(ExpandConstant('{commonappdata}\ITSeti\Maintenance')) then
        RaiseException('Не удалось создать каталог инвентарного номера.');
      if not SaveStringToFile(ExpandConstant('{commonappdata}\ITSeti\Maintenance\inventory.txt'),
        Trim(InventoryEdit.Text), False) then
        RaiseException('Не удалось сохранить инвентарный номер.');
    end;
  end;
  if CurStep = ssPostInstall then
  begin
    WizardForm.ProgressGauge.Style := npbstMarquee;
    WizardForm.StatusLabel.Caption := 'Настраиваем обслуживание компьютера. Это может занять около минуты...';
    MaintenanceResult := ExpandConstant('{commonappdata}\ITSeti\Maintenance\install-error.txt');
    DeleteFile(MaintenanceResult);
    if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
      '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{tmp}\ITSeti-Package\Install-Maintenance.ps1') +
      '" -PackageRoot "' + ExpandConstant('{tmp}\ITSeti-Package') + '" -InstallRoot "' + ExpandConstant('{app}') + '" -PreinstalledApp -ResultFile "' + MaintenanceResult + '"',
      '', SW_HIDE, ewWaitUntilTerminated, Code) or (Code <> 0) then
    begin
      if not LoadStringFromFile(MaintenanceResult, MaintenanceText) then
        MaintenanceText := 'PowerShell не записал подробности. Проверьте C:\ProgramData\ITSeti\Maintenance\install.log.';
      RaiseException('Установка приложения не завершена. Код: ' + IntToStr(Code) + #13#10 + MaintenanceText);
    end;
    if not WizardSilent then
    begin
      WizardForm.StatusLabel.Caption := 'Подключение компьютера к серверу...';
      ConnectCode := -1;
      if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
        '-STA -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "' + ExpandConstant('{app}\Connect-Server.ps1') + '" -SkipIfConnected',
        '', SW_SHOWNORMAL, ewWaitUntilTerminated, ConnectCode) or (ConnectCode <> 0) then
        MsgBox('Не удалось открыть подключение к серверу. Локальная установка завершена; подключить ПК можно позже через меню «Пуск».', mbInformation, MB_OK);
    end;
    if SoftwareCheck.Checked then
    begin
      WizardForm.StatusLabel.Caption := 'Устанавливаем выбранные программы. Дождитесь результата...';
      ResultPath := ExpandConstant('{tmp}\org-install-result.txt');
      SoftwareCode := 2;
      if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
        '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{app}\Backend\Install-OrganizationSoftware.ps1') +
        '" -InstallerDirectory "' + ExpandConstant('{tmp}\ITSeti-Package\Setup\ITSETI-Setup') + '" -ResultFile "' + ResultPath + '"',
        '', SW_HIDE, ewWaitUntilTerminated, SoftwareCode) or (SoftwareCode <> 0) then
      begin
        if not LoadStringFromFile(ResultPath, ResultText) then
          ResultText := 'No detailed result from package installer.';
        MsgBox('Программы ИТ-Сети не установлены. Приложение обслуживания установлено.' + #13#10 + #13#10 + ResultText,
          mbError, MB_OK);
      end
      else
        MsgBox('Программы ИТ-Сети установлены.', mbInformation, MB_OK);
    end;
    WizardForm.ProgressGauge.Style := npbstNormal;
    WizardForm.ProgressGauge.Position := WizardForm.ProgressGauge.Max;
    WizardForm.StatusLabel.Caption := 'Установка завершена.';
  end;
end;
