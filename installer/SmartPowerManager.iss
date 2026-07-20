; SmartPowerManager - Inno Setup installer definition
; Usage: scripts\build-installer.ps1

#define MyAppName "SmartPowerManager"
#define MyAppVersion "2.0.15"
#define MyAppPublisher "kazu-1234"
#define MyAppURL "https://github.com/kazu-1234/SmartPowerManager"
#define MyAppExeName "SmartPowerManager.exe"
#define PublishDir "..\dist\folder"

[Setup]
AppId={{B7E4D3F2-8C15-4E7B-A1D9-6F2A9C0B4E88}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE_NOTICE.txt
OutputDir=..\dist\installer
OutputBaseFilename=SmartPowerManager-v{#MyAppVersion}-win-x64-setup
SetupIconFile=..\CSharp\Assets\app_icon.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
InfoAfterFile=
CloseApplications=force
RestartApplications=no
UsePreviousAppDir=yes

[Languages]
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Parameters: "--sync-autostart"; Flags: runhidden waituntilterminated
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\{#MyAppExeName}"; Parameters: "--cleanup-autostart"; Flags: runhidden waituntilterminated; RunOnceId: "CleanupAutostart"

[UninstallDelete]
; App leftovers (not run on upgrade)
Type: filesandordirs; Name: "{app}"

[Code]
procedure TerminateApp;
var
  ResultCode: Integer;
  ExePath: String;
begin
  ExePath := ExpandConstant('{localappdata}\Programs\{#MyAppName}\{#MyAppExeName}');
  if FileExists(ExePath) then
    Exec(ExePath, '--exit', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#MyAppExeName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(800);
end;

function InitializeSetup(): Boolean;
begin
  TerminateApp;
  Result := True;
end;

function InitializeUninstall(): Boolean;
begin
  TerminateApp;
  Result := True;
end;

// On upgrade: clean {app} but keep %AppData% settings
procedure PreserveUserDataFile(const FileName: String);
var
  Src, DestDir, Dest: String;
begin
  Src := ExpandConstant('{app}\' + FileName);
  DestDir := ExpandConstant('{userappdata}\{#MyAppName}');
  Dest := DestDir + '\' + FileName;
  if FileExists(Src) then
  begin
    ForceDirectories(DestDir);
    // Do not overwrite existing AppData settings
    if not FileExists(Dest) then
      CopyFile(Src, Dest, False);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
  begin
    if DirExists(ExpandConstant('{app}')) then
    begin
      // Migrate legacy settings from {app} to AppData, then clean {app}
      PreserveUserDataFile('settings.json');
      PreserveUserDataFile('schedules.json');
      DelTree(ExpandConstant('{app}'), True, True, True);
    end;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  // Delete user settings only on uninstall (not on upgrade)
  if CurUninstallStep = usPostUninstall then
    DelTree(ExpandConstant('{userappdata}\{#MyAppName}'), True, True, True);
end;
