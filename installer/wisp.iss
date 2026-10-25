#define MyAppName "Wisp"
#define MyAppVersion "0.1.0"
#define MyAppPublisher "Wisp"
#define MyAppExeName "Wisp.exe"

[Setup]
AppId={{B3B3A3D1-6E7C-4D6A-9B0E-2E7B8D33F1A1}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
OutputDir=..\dist
OutputBaseFilename=Wisp-Setup-{#MyAppVersion}
SetupIconFile=..\dotnet\src\Wisp.Windows\Assets\AppIcon.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
ArchitecturesAllowed=x64compatible
MinVersion=10.0.22000
AppMutex=WispAppMutex

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional icons:"; Flags: unchecked
Name: "startup"; Description: "Start Wisp when I sign in"; GroupDescription: "Startup:"; Flags: unchecked

[Files]
Source: "..\dotnet\src\Wisp.Windows\bin\x64\Release\net10.0-windows10.0.19041.0\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "deps\windowsdesktop-runtime-10.0.13-win-x64.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall
Source: "deps\WindowsAppRuntimeInstall-x64.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall

[Icons]
Name: "{autoprograms}\Wisp"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{autoprograms}\Uninstall Wisp"; Filename: "{uninstallexe}"
Name: "{autodesktop}\Wisp"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Wisp"; ValueData: """{app}\{#MyAppExeName}"""; Flags: uninsdeletevalue; Tasks: startup

[Run]
Filename: "{tmp}\windowsdesktop-runtime-10.0.13-win-x64.exe"; Parameters: "/install /quiet /norestart"; StatusMsg: "Installing .NET 10 Desktop Runtime..."; Flags: waituntilterminated runhidden
Filename: "{tmp}\WindowsAppRuntimeInstall-x64.exe"; Parameters: "--quiet"; StatusMsg: "Installing Windows App Runtime..."; Flags: waituntilterminated runhidden
Filename: "{app}\{#MyAppExeName}"; Description: "Launch Wisp"; Flags: nowait postinstall skipifsilent
