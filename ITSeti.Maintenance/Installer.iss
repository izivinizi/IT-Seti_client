#define PackageRoot "dist\ITSeti-Maintenance"

[Setup]
AppId={{CFA7B53D-16A9-4D77-9D82-EE68369AB185}
AppName=ИТ-Сети Обслуживание ПК
AppVersion=0.9.5
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
Source: "Uninstall-Maintenance.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "Uninstall-Options.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "OrganizationUninstall.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "Uninstall-ITSeti.cmd"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\ИТ-Сети Обслуживание ПК"; Filename: "{app}\ITSeti.Maintenance.exe"
Name: "{autodesktop}\ИТ-Сети Обслуживание ПК"; Filename: "{app}\ITSeti.Maintenance.exe"
Name: "{autoprograms}\Удалить ИТ-Сети"; Filename: "{app}\Uninstall-ITSeti.cmd"; IconFilename: "{app}\ITSeti.Maintenance.exe"

[UninstallRun]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\Uninstall-Maintenance.ps1"""; Flags: runhidden waituntilterminated; RunOnceId: "ITSetiMaintenanceCleanup"

[Code]
var
  OptionsPage: TWizardPage;
  InstallDirEdit: TNewEdit;
  InstallDirBrowse: TNewButton;
  SoftwareCheck: TNewCheckBox;
  SoftwareDirEdit: TNewEdit;
  SoftwareDirBrowse: TNewButton;
  InventoryEdit: TNewEdit;

procedure BrowseInstallDir(Sender: TObject);
var
  Path: String;
begin
  Path := InstallDirEdit.Text;
  if BrowseForFolder('Папка установки приложения', Path, True) then InstallDirEdit.Text := Path;
end;

procedure BrowseSoftwareDir(Sender: TObject);
var
  Path: String;
begin
  Path := SoftwareDirEdit.Text;
  if BrowseForFolder('Папка комплекта ITSETI-Setup', Path, False) then SoftwareDirEdit.Text := Path;
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
  SourceDirectory: String;
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

  AddLabel('Папка комплекта ITSETI-Setup', 106);
  SoftwareDirEdit := TNewEdit.Create(OptionsPage);
  SoftwareDirEdit.Parent := OptionsPage.Surface;
  SoftwareDirEdit.Left := 0;
  SoftwareDirEdit.Top := 128;
  SoftwareDirEdit.Width := OptionsPage.SurfaceWidth - 90;
  SourceDirectory := ExpandConstant('{src}');
  if not FileExists(AddBackslash(SourceDirectory) + 'ITSETI-Setup\system\Install.ps1') then
    SourceDirectory := ExtractFileDir(SourceDirectory);
  if not FileExists(AddBackslash(SourceDirectory) + 'ITSETI-Setup\system\Install.ps1') then
    SourceDirectory := ExtractFileDir(SourceDirectory);
  SoftwareDirEdit.Text := AddBackslash(SourceDirectory) + 'ITSETI-Setup';
  SoftwareDirBrowse := TNewButton.Create(OptionsPage);
  SoftwareDirBrowse.Parent := OptionsPage.Surface;
  SoftwareDirBrowse.Left := SoftwareDirEdit.Width + 8;
  SoftwareDirBrowse.Top := 126;
  SoftwareDirBrowse.Width := 82;
  SoftwareDirBrowse.Height := 27;
  SoftwareDirBrowse.Caption := 'Обзор...';
  SoftwareDirBrowse.OnClick := @BrowseSoftwareDir;

  AddLabel('Инвентарный номер (четыре цифры, необязательно)', 181);
  InventoryEdit := TNewEdit.Create(OptionsPage);
  InventoryEdit.Parent := OptionsPage.Surface;
  InventoryEdit.Left := 0;
  InventoryEdit.Top := 204;
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
  if SoftwareCheck.Checked and not FileExists(AddBackslash(Trim(SoftwareDirEdit.Text)) + 'system\Install.ps1') then
  begin
    MsgBox('Не найден комплект ITSETI-Setup в выбранной папке.', mbError, MB_OK);
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
begin
  if CurStep = ssPostInstall then
  begin
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
    if Trim(InventoryEdit.Text) <> '' then
      if not SaveStringToFile(ExpandConstant('{commonappdata}\ITSeti\Maintenance\inventory.txt'),
        Trim(InventoryEdit.Text), False) then
        RaiseException('Не удалось сохранить инвентарный номер.');
    if SoftwareCheck.Checked then
    begin
      ResultPath := ExpandConstant('{tmp}\org-install-result.txt');
      SoftwareCode := 2;
      if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
        '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{tmp}\ITSeti-Package\Install-OrganizationSoftware.ps1') +
        '" -InstallerDirectory "' + Trim(SoftwareDirEdit.Text) + '" -ResultFile "' + ResultPath + '"',
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
  end;
end;
