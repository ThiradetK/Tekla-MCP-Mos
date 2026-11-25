; Tekla MCP Server installer (Inno Setup 6). Per-user install, no admin.
; Build: powershell -ExecutionPolicy Bypass -File build.ps1 -Installer   (output: dist\TeklaMcp-Setup-<version>.exe)
; Ships only TeklaMcp and its NuGet dependencies; Tekla/Trimble DLLs come from the local Tekla installation at runtime.

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#define BinDir "..\src\TeklaMcp\bin\Release\net48"

[Setup]
AppId={{6F0B7C1E-4E52-4E0B-9C38-2E9A6E1D7A41}
AppName=Tekla MCP Server
AppVersion={#AppVersion}
AppPublisher=TeklaMcp
DefaultDirName={localappdata}\Programs\TeklaMcp
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist
OutputBaseFilename=TeklaMcp-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName=Tekla MCP Server {#AppVersion}
UninstallDisplayIcon={app}\TeklaMcp.exe
CloseApplications=yes
RestartApplications=no

[Tasks]
Name: "desktop";  Description: "Register with Claude Desktop"
Name: "code";     Description: "Register with Claude Code (needs the 'claude' command)"; Flags: unchecked
Name: "readonly"; Description: "Read-only mode (block all tools that change the Tekla model)"; Flags: unchecked

[Files]
Source: "{#BinDir}\*"; Excludes: "*.pdb,Tekla.*.dll,Trimble.*.dll"; DestDir: "{app}"; Flags: ignoreversion

[Run]
Filename: "{app}\TeklaMcp.exe"; Parameters: "--register desktop"; Tasks: desktop;  Flags: runhidden; StatusMsg: "Registering with Claude Desktop..."
Filename: "{app}\TeklaMcp.exe"; Parameters: "--register code";    Tasks: code;     Flags: runhidden; StatusMsg: "Registering with Claude Code..."
Filename: "{app}\TeklaMcp.exe"; Parameters: "--readonly on";      Tasks: readonly; Flags: runhidden

[UninstallRun]
Filename: "{app}\TeklaMcp.exe"; Parameters: "--unregister all"; Flags: runhidden; RunOnceId: "UnregisterTeklaMcp"

[Messages]
FinishedLabel=Setup has installed Tekla MCP Server.%n%nRestart Claude Desktop (Quit from the tray icon, then start it again) to load the 'tekla' server.%n%nTo check the setup, run in a terminal:%n"{app}\TeklaMcp.exe" --doctor

[Code]
function InitializeSetup(): Boolean;
var
  Release: Cardinal;
begin
  Result := True;
  if not (RegQueryDWordValue(HKLM, 'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full', 'Release', Release) and (Release >= 528040)) then
  begin
    MsgBox('.NET Framework 4.8 is required. Install it from Microsoft, then run this setup again.', mbCriticalError, MB_OK);
    Result := False;
    exit;
  end;
  if not RegKeyExists(HKLM64, 'SOFTWARE\Trimble\Tekla Structures\2026.0\setup') then
    Result := MsgBox('Tekla Structures 2026 was not found on this computer. The server needs it to work.' + #13#10#13#10 + 'Install anyway?',
                     mbConfirmation, MB_YESNO) = IDYES;
end;
