; Script Inno Setup untuk Aplikasi Desktop Atlaz
; Dihasilkan secara otomatis untuk rilis installer Windows

[Setup]
AppId={{9F5781E2-A2D0-4F8A-B471-12C85C0825B0}
AppName=Atlaz
AppVersion=1.0.0
AppVerName=Atlaz v1.0.0
AppPublisher=Atlaz
DefaultDirName={autopf}\Atlaz
DefaultGroupName=Atlaz
DisableProgramGroupPage=yes
OutputDir=c:\xampp\htdocs\atlaz\installer
OutputBaseFilename=Atlaz_Setup_v1.0
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64
PrivilegesRequired=lowest

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "c:\xampp\htdocs\atlaz\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Atlaz"; Filename: "{app}\Atlaz.UI.exe"
Name: "{autodesktop}\Atlaz"; Filename: "{app}\Atlaz.UI.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Atlaz.UI.exe"; Description: "{cm:LaunchProgram,Atlaz}"; Flags: nowait postinstall skipifsilent

