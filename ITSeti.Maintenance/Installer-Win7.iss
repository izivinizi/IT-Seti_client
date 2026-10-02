#define PackageRoot "dist\Win7\Package"

[Setup]
AppId={{9C83D41A-9753-4E64-8EC8-A9A324BD7D52}
AppName=ИТ-Сети Обслуживание ПК (Windows 7 beta)
AppVersion=0.3.0
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
UninstallDisplayIcon={app}\ITSeti.Maintenance.Win7.exe

[Languages]
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"

[Files]
Source: "{#PackageRoot}\App\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PackageRoot}\Tools\CrystalDiskInfo\*"; DestDir: "{app}\Tools\CrystalDiskInfo"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PackageRoot}\Tools\DiskSpd\*"; DestDir: "{app}\Tools\DiskSpd"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\ИТ-Сети Обслуживание ПК (Windows 7)"; Filename: "{app}\ITSeti.Maintenance.Win7.exe"
Name: "{autodesktop}\ИТ-Сети Обслуживание ПК (Windows 7)"; Filename: "{app}\ITSeti.Maintenance.Win7.exe"

[Run]
Filename: "{app}\ITSeti.Maintenance.Win7.exe"; Description: "Запустить приложение"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
Filename: "{app}\ITSeti.Maintenance.Win7.exe"; Parameters: "--unregister-smart-task"; Flags: runhidden waituntilterminated; RunOnceId: "Win7SmartTask"

[Code]
function InitializeSetup: Boolean;
begin
  Result := IsDotNetInstalled(net48, 0);
  if not Result then
    MsgBox('Для Windows 7 требуется .NET Framework 4.8. Установите его с сайта Microsoft и повторите установку.', mbCriticalError, MB_OK);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ExitCode: Integer;
begin
  if CurStep <> ssPostInstall then Exit;
  ExitCode := -1;
  if not Exec(ExpandConstant('{app}\ITSeti.Maintenance.Win7.exe'), '--register-smart-task', '', SW_HIDE, ewWaitUntilTerminated, ExitCode) or (ExitCode <> 0) then
    RaiseException('Не удалось зарегистрировать задачу SMART. Подробности: ' + ExpandConstant('{app}\SmartTaskSetup.error.txt'));
end;
