#define PackageRoot "dist\Win7\Package"

[Setup]
AppId={{9C83D41A-9753-4E64-8EC8-A9A324BD7D52}
AppName=ИТ-Сети Обслуживание ПК (Windows 7 beta)
AppVersion=2.4.8
AppPublisher=ИТ-Сети
DefaultDirName={autopf}\ITSeti Maintenance Win7
DisableDirPage=yes
UsePreviousAppDir=no
DefaultGroupName=ИТ-Сети
OutputDir=dist\Win7
OutputBaseFilename=ITSeti-Maintenance-Win7-Setup
SetupIconFile=src\ITSeti.Maintenance.App\Assets\favicon.ico
PrivilegesRequired=admin
MinVersion=6.1sp1
OnlyBelowVersion=6.2
ArchitecturesAllowed=x86compatible
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\ITSeti.Maintenance.Win7.Port.exe

[Languages]
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"

[Files]
Source: "{#PackageRoot}\Prerequisites\NDP48-x86-x64-AllOS-ENU.exe"; Flags: dontcopy noencryption
Source: "{#PackageRoot}\App\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PackageRoot}\Tools\CrystalDiskInfo\*"; DestDir: "{app}\Tools\CrystalDiskInfo"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PackageRoot}\Tools\CrystalDiskMark9\*"; DestDir: "{app}\Tools\CrystalDiskMark9"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PackageRoot}\Tools\DiskSpd\*"; DestDir: "{app}\Tools\DiskSpd"; Flags: ignoreversion
Source: "{#PackageRoot}\Tools\HWiNFO\*"; DestDir: "{app}\Tools\HWiNFO"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\ИТ-Сети Обслуживание ПК (Windows 7)"; Filename: "{app}\ITSeti.Maintenance.Win7.Port.exe"
Name: "{autodesktop}\ИТ-Сети Обслуживание ПК (Windows 7)"; Filename: "{app}\ITSeti.Maintenance.Win7.Port.exe"

[Run]
Filename: "{app}\ITSeti.Maintenance.Win7.Port.exe"; Description: "Запустить приложение"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
Filename: "{sys}\sc.exe"; Parameters: "stop ITSetiMaintenanceWin7"; Flags: runhidden waituntilterminated; RunOnceId: "Win7MaintenanceServiceStop"
Filename: "{sys}\sc.exe"; Parameters: "delete ITSetiMaintenanceWin7"; Flags: runhidden waituntilterminated; RunOnceId: "Win7MaintenanceServiceDelete"
Filename: "{app}\ITSeti.Maintenance.Win7.Port.exe"; Parameters: "--unregister-smart-task"; Flags: runhidden waituntilterminated; RunOnceId: "Win7SmartTask"

[Code]
var
  InventoryPage: TInputQueryWizardPage;

procedure InitializeWizard;
var
  Existing: AnsiString;
begin
  InventoryPage := CreateInputQueryPage(wpWelcome, 'Данные компьютера',
    'Укажите инвентарный номер, если он есть.',
    'Номер сохраняется для всех пользователей и плановых проверок.');
  InventoryPage.Add('Инвентарный номер (четыре цифры, необязательно)', False);
  InventoryPage.Edits[0].MaxLength := 4;
  if LoadStringFromFile(ExpandConstant('{commonappdata}\ITSeti\Maintenance\inventory.txt'), Existing) then
    InventoryPage.Values[0] := Trim(Existing);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Value: String;
  I: Integer;
begin
  Result := True;
  if CurPageID <> InventoryPage.ID then Exit;
  Value := Trim(InventoryPage.Values[0]);
  if Value = '' then Exit;
  Result := Length(Value) = 4;
  if Result then
    for I := 1 to Length(Value) do
      if (Value[I] < '0') or (Value[I] > '9') then Result := False;
  if not Result then MsgBox('Введите ровно четыре цифры.', mbError, MB_OK);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ExitCode: Integer;
  I: Integer;
begin
  Result := '';
  if Exec(ExpandConstant('{sys}\sc.exe'), 'query ITSetiMaintenanceWin7', '', SW_HIDE,
    ewWaitUntilTerminated, ExitCode) and (ExitCode = 0) then
  begin
    Exec(ExpandConstant('{sys}\sc.exe'), 'stop ITSetiMaintenanceWin7', '', SW_HIDE,
      ewWaitUntilTerminated, ExitCode);
    for I := 1 to 120 do
    begin
      if not (Exec(ExpandConstant('{sys}\sc.exe'), 'query ITSetiMaintenanceWin7', '', SW_HIDE,
        ewWaitUntilTerminated, ExitCode) and (ExitCode = 0)) then Break;
      Sleep(250);
    end;
  end;
  if IsDotNetInstalled(net48, 0) then Exit;

  ExtractTemporaryFile('NDP48-x86-x64-AllOS-ENU.exe');
  ExitCode := -1;
  if not Exec(ExpandConstant('{tmp}\NDP48-x86-x64-AllOS-ENU.exe'),
    '/passive /norestart /ChainingPackage ITSetiMaintenanceWin7', '', SW_SHOW,
    ewWaitUntilTerminated, ExitCode) then
  begin
    Result := 'Не удалось запустить установку .NET Framework 4.8.';
    Exit;
  end;
  if (ExitCode = 3010) or (ExitCode = 1641) then
  begin
    NeedsRestart := True;
    Result := '.NET Framework 4.8 установлен. Перезагрузите Windows и повторно запустите установщик ИТ-Сети.';
    Exit;
  end;
  if ExitCode <> 0 then
  begin
    Result := 'Установка .NET Framework 4.8 не завершилась (код ' + IntToStr(ExitCode) +
      '). На Windows 7 SP1 проверьте обновления SHA-2 KB4474419 и KB4490628.';
    Exit;
  end;
  if not IsDotNetInstalled(net48, 0) then
  begin
    NeedsRestart := True;
    Result := '.NET Framework 4.8 не обнаружен после установки. Перезагрузите Windows и повторите попытку.';
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ExitCode: Integer;
  ServiceParams: String;
begin
  if CurStep = ssInstall then
  begin
    if not ForceDirectories(ExpandConstant('{commonappdata}\ITSeti\Maintenance')) then
      RaiseException('Не удалось создать каталог данных обслуживания.');
    if not Exec(ExpandConstant('{sys}\icacls.exe'),
      '"' + ExpandConstant('{commonappdata}\ITSeti') + '" /grant "*S-1-5-18:(OI)(CI)F" /T /C',
      '', SW_HIDE, ewWaitUntilTerminated, ExitCode) or (ExitCode <> 0) then
      RaiseException('Не удалось восстановить доступ службы SYSTEM к данным обслуживания Windows 7 (код ' + IntToStr(ExitCode) + ').');
    if Trim(InventoryPage.Values[0]) <> '' then
    begin
      if not ForceDirectories(ExpandConstant('{commonappdata}\ITSeti\Maintenance')) then
        RaiseException('Не удалось создать каталог инвентарного номера.');
      if not SaveStringToFile(ExpandConstant('{commonappdata}\ITSeti\Maintenance\inventory.txt'),
        Trim(InventoryPage.Values[0]), False) then
        RaiseException('Не удалось сохранить инвентарный номер.');
    end;
  end;
  if CurStep <> ssPostInstall then Exit;
  ServiceParams := 'create ITSetiMaintenanceWin7 binPath= "' +
    ExpandConstant('{app}\ITSeti.Maintenance.Win7.Port.exe') +
    '" --service start= auto obj= LocalSystem DisplayName= "ИТ-Сети Обслуживание Windows 7"';
  if not Exec(ExpandConstant('{sys}\sc.exe'), 'query ITSetiMaintenanceWin7', '', SW_HIDE,
    ewWaitUntilTerminated, ExitCode) or (ExitCode <> 0) then
  begin
    if not Exec(ExpandConstant('{sys}\sc.exe'), ServiceParams, '', SW_HIDE,
      ewWaitUntilTerminated, ExitCode) or (ExitCode <> 0) then
      RaiseException('Не удалось установить службу обслуживания Windows 7 (код ' + IntToStr(ExitCode) + ').');
  end
  else if not Exec(ExpandConstant('{sys}\sc.exe'),
    'config ITSetiMaintenanceWin7 binPath= "' + ExpandConstant('{app}\ITSeti.Maintenance.Win7.Port.exe') +
    '" --service start= auto obj= LocalSystem', '', SW_HIDE, ewWaitUntilTerminated, ExitCode) or (ExitCode <> 0) then
    RaiseException('Не удалось обновить службу обслуживания Windows 7 (код ' + IntToStr(ExitCode) + ').');
  if not Exec(ExpandConstant('{sys}\sc.exe'), 'start ITSetiMaintenanceWin7', '', SW_HIDE,
    ewWaitUntilTerminated, ExitCode) or (ExitCode <> 0) then
    RaiseException('Не удалось запустить службу обслуживания Windows 7 (код ' + IntToStr(ExitCode) + ').');
  ExitCode := -1;
  if not Exec(ExpandConstant('{app}\ITSeti.Maintenance.Win7.Port.exe'), '--register-smart-task', '', SW_HIDE, ewWaitUntilTerminated, ExitCode) or (ExitCode <> 0) then
    RaiseException('Не удалось зарегистрировать задачу SMART. Подробности: ' + ExpandConstant('{app}\SmartTaskSetup.error.txt'));
end;
