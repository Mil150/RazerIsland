; Інсталятор Razer Island (Inno Setup 6).
; Збирається через build.ps1 у корені репозиторію; можна й вручну:
;   dotnet publish src\RazerIsland -c Release -r win-x64 --self-contained -o artifacts\publish
;   ISCC installer\RazerIsland.iss

#define AppName      "Razer Island"
#define AppExe       "RazerIsland.exe"
#define AppPublisher "Mil150"
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish"
#endif
#ifndef AppVersion
  #define AppVersion GetStringFileInfo(AddBackslash(PublishDir) + AppExe, "ProductVersion")
#endif
; м'ютекс запущеного віджета (див. Core\SingleInstance.cs)
#define AppMutexName "Local\RazerBatteryWidget_mil"

[Setup]
AppId={{6F1B3C2A-8E4D-4B7A-9C5E-2D7F0A1B4C93}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppComments=Заряд Razer, годинник, погода, розклад і мікрофон Discord
VersionInfoVersion={#AppVersion}
; без прав адміністратора: %LOCALAPPDATA%\Programs\Razer Island
PrivilegesRequired=lowest
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
SetupIconFile=..\src\RazerIsland\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
OutputDir=..\artifacts\installer
OutputBaseFilename=RazerIsland-{#AppVersion}-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=no

[Languages]
Name: "uk"; MessagesFile: "compiler:Languages\Ukrainian.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
uk.AppDescription=Заряд Razer, годинник, погода, розклад і мікрофон Discord
uk.SettingsShortcut=налаштування
en.AppDescription=Razer battery, clock, weather, schedule and Discord microphone
en.SettingsShortcut=Settings

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\CHANGELOG.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
; ті самі імена, що й у PowerShell-версії, — старі ярлики замінюються
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; Comment: "{cm:AppDescription}"
Name: "{autoprograms}\{#AppName} — {cm:SettingsShortcut}"; Filename: "{app}\{#AppExe}"; Parameters: "-OpenSettings"; Comment: "{cm:SettingsShortcut}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[Code]
const
  EVENT_MODIFY_STATE = $0002;
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  RunName = 'RazerBatteryWidget';

function OpenEvent(dwDesiredAccess: DWORD; bInheritHandle: BOOL; lpName: string): THandle;
  external 'OpenEventW@kernel32.dll stdcall';
function SetEvent(hEvent: THandle): BOOL;
  external 'SetEvent@kernel32.dll stdcall';
function CloseHandle(hObject: THandle): BOOL;
  external 'CloseHandle@kernel32.dll stdcall';

{ Попросити запущений віджет завершитися (як при повторному запуску) і дочекатися. }
procedure StopRunningApp();
var
  h: THandle;
  i: Integer;
begin
  if not CheckForMutexes('{#AppMutexName}') then exit;
  h := OpenEvent(EVENT_MODIFY_STATE, False, '{#AppMutexName}_quit');
  if h <> 0 then
  begin
    SetEvent(h);
    CloseHandle(h);
  end;
  for i := 1 to 50 do
  begin
    if not CheckForMutexes('{#AppMutexName}') then exit;
    Sleep(100);
  end;
end;

{ Стара PowerShell-версія тримає той самий м'ютекс, але живе в іншій теці й сигналу не слухає —
  її закриє сам новий exe під час першого запуску, тож встановлення не блокуємо. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopRunningApp();
  Result := '';
end;

function InitializeUninstall(): Boolean;
begin
  StopRunningApp();
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  cmd: String;
begin
  if CurUninstallStep = usPostUninstall then
    { автозапуск прибираємо лише якщо він вказує на цю копію }
    if RegQueryStringValue(HKCU, RunKey, RunName, cmd) and (Pos(Lowercase(ExpandConstant('{app}')), Lowercase(cmd)) > 0) then
      RegDeleteValue(HKCU, RunKey, RunName);
end;
