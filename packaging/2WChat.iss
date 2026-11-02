#ifndef AppVersion
  #define AppVersion "1.11.0"
#endif
#ifdef SmokeTestRoot
  #define Identity "2WChat-Installer-Smoke"
  #define OutputName "2WChat-" + AppVersion + "-Setup-test"
#else
  #define Identity "{{B383F621-7B71-44F5-87C6-4E7C609C4B10}"
  #define OutputName "2WChat-" + AppVersion + "-Setup"
#endif

[Setup]
AppId={#Identity}
AppName=2WChat
AppVersion={#AppVersion}
AppPublisher=2WChat
AppPublisherURL=https://github.com/merumuiri-afk/2wchat2
AppUpdatesURL=https://github.com/merumuiri-afk/2wchat2/releases
DefaultDirName={localappdata}\Programs\2WChat
DefaultGroupName=2WChat
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
DisableProgramGroupPage=yes
DisableDirPage=no
WizardStyle=classic
OutputDir=..\dist
OutputBaseFilename={#OutputName}
SetupIconFile=..\src\TwoWChat\Assets\app.ico
UninstallDisplayIcon={app}\2WChat.exe
Compression=zip/1
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Создать ярлык на рабочем столе"; GroupDescription: "Ярлыки:"

[Files]
Source: "..\dist\2WChat\2WChat.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\dist\2WChat\*.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\dist\2WChat\Allow-Firewall.*"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\dist\2WChat\licenses\*"; DestDir: "{app}\licenses"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
#ifdef SmokeTestRoot
Name: "{#SmokeTestRoot}\Desktop\2WChat"; Filename: "{app}\2WChat.exe"; WorkingDir: "{app}"; Tasks: desktopicon
Name: "{#SmokeTestRoot}\StartMenu\2WChat"; Filename: "{app}\2WChat.exe"; WorkingDir: "{app}"
#else
Name: "{userdesktop}\2WChat"; Filename: "{app}\2WChat.exe"; WorkingDir: "{app}"; Tasks: desktopicon
Name: "{userprograms}\2WChat\2WChat"; Filename: "{app}\2WChat.exe"; WorkingDir: "{app}"
#endif

[Run]
Filename: "{app}\2WChat.exe"; Description: "Запустить 2WChat"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: files; Name: "{app}\2WChat.exe.previous"
