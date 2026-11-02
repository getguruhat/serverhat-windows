#define AppName "ServerHat"
#define AppVersion "0.2.1"
[Setup]
AppId={{E89CC74B-BB7E-4305-BC06-B0161E8D3977}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
VersionInfoVersion={#AppVersion}
VersionInfoProductVersion={#AppVersion}
AppPublisher=GuruHat
AppPublisherURL=https://guruhat.com/
DefaultDirName={autopf}\GuruHat\ServerHat
DefaultGroupName=ServerHat
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=dist
OutputBaseFilename=ServerHat-{#AppVersion}-Windows-Setup
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
SetupIconFile=assets\ServerHat.ico
UninstallDisplayIcon={app}\ServerHat.exe
CloseApplications=force
RestartApplications=no
[Files]
Source: "publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
[Icons]
Name: "{group}\ServerHat"; Filename: "{app}\ServerHat.exe"
[Tasks]
Name: "startup"; Description: "Start ServerHat when I sign in to Windows"
[Registry]
; The same value the "Open ServerHat when I sign in to Windows" checkbox writes.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "GuruHat ServerHat"; ValueData: """{app}\ServerHat.exe"""; Tasks: startup; Flags: uninsdeletevalue
[Run]
Filename: "{app}\ServerHat.exe"; Description: "Launch ServerHat"; Flags: nowait postinstall skipifsilent
[Code]
// ServerHat needs .NET Framework 4.8 (release 528040). It's part of Windows 10 1903 and later
// and Windows 11, so this only stops PCs on older Windows 10 builds that never installed it.
function InitializeSetup(): Boolean;
var
  Release: Cardinal;
  ErrorCode: Integer;
begin
  Result := RegQueryDWordValue(HKLM, 'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full', 'Release', Release) and (Release >= 528040);
  if not Result then
    if MsgBox('ServerHat needs Microsoft .NET Framework 4.8, which isn''t installed on this PC.' + #13#10#13#10 +
              'Open the Microsoft download page now?', mbError, MB_YESNO) = IDYES then
      ShellExecAsOriginalUser('open', 'https://dotnet.microsoft.com/download/dotnet-framework/net48', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
end;
