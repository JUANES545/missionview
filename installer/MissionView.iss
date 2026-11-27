#ifndef AppVersion
  #error AppVersion must come from the release plan.
#endif
#ifndef PublishDir
  #error PublishDir must point to a verified self-contained publish.
#endif
#ifndef OutputDir
  #error OutputDir is required.
#endif

[Setup]
AppId={{E983258E-4F39-4514-96FD-BCC8D270BAAF}
AppName=MissionView
AppVersion={#AppVersion}
VersionInfoVersion={#AppVersion}.0
AppPublisher=MissionView contributors
AppPublisherURL=https://github.com/JUANES545/missionview
DefaultDirName={localappdata}\Programs\MissionView
DefaultGroupName=MissionView
DisableDirPage=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.26100
LicenseFile={#PublishDir}\LICENSE
OutputDir={#OutputDir}
OutputBaseFilename=MissionView-{#AppVersion}-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\assets\branding\MissionView.ico
UninstallDisplayIcon={app}\MissionView.exe
CloseApplications=yes
CloseApplicationsFilter=WindowScatter.exe,MissionView.exe
RestartApplications=no

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb,settings.json,*diagnostic*.json"

[InstallDelete]
Type: files; Name: "{app}\WindowScatter.exe"
Type: files; Name: "{app}\WindowScatter.dll"
Type: files; Name: "{app}\WindowScatter.deps.json"
Type: files; Name: "{app}\WindowScatter.runtimeconfig.json"

[Icons]
Name: "{group}\MissionView"; Filename: "{app}\MissionView.exe"; WorkingDir: "{app}"

[Run]
Filename: "{app}\MissionView.exe"; Description: "Reopen MissionView"; Flags: postinstall nowait skipifsilent runasoriginaluser; Check: RestartRequested

[Code]
function RestartRequested: Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), '/RESTARTMISSIONVIEW') = 0 then
    begin
      Result := True;
      Exit;
    end;
end;
