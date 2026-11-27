; Inno Setup script for DBooyvol (unpackaged, per-user install).
;
; Prerequisites:
;   1. dotnet publish src\DBooyvol.App -c Release -r win-x64 --self-contained
;   2. powershell -File runtime\make-jlink.ps1
;   3. powershell -File sidecar\build.ps1
;
; Then compile with:
;   iscc installer\DBooyvol.iss /DPublishDir=..\publish

#ifndef PublishDir
  #define PublishDir "..\publish"
#endif
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

#define AppName "DBooyvol"
#define AppExe "DBooyvol.exe"

[Setup]
AppId={{8E5C5F6A-4C7B-4D2E-9F1A-3B6C0D9E7A21}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=DBooyvol
DefaultDirName={autopf}\DBooyvol
DefaultGroupName=DBooyvol
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=..\build\installer
OutputBaseFilename=DBooyvol-{#AppVersion}-win-x64
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#AppExe}
; Signing is applied by CI (signtool) or by ISCC /S when a certificate is configured.
; SignTool=signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 $f

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\runtime\jre\*"; DestDir: "{app}\runtime"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\sidecar\build\sidecar.jar"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\drivers\*.jar"; DestDir: "{app}\drivers"; Flags: ignoreversion
Source: "..\resources\locales\*"; DestDir: "{app}\locales"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}\runtime"
