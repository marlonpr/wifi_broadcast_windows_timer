#define MyAppName "Factory Timer"
#define MyAppPublisher "Factory Timer"
#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\\artifacts\\publish\\win-x64"
#endif
#ifndef PrereqDir
  #define PrereqDir "prereqs"
#endif
#ifndef OutputDir
  #define OutputDir "..\\artifacts\\installer"
#endif

[Setup]
AppId={{B78FD2F7-B36E-4B8B-8B74-A49E38F7D7B1}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\Factory Timer
DefaultGroupName=Factory Timer
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename=FactoryTimerSetup-{#MyAppVersion}-win-x64
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
UninstallDisplayName=Factory Timer
UninstallDisplayIcon={app}\FactoryTimer.Controller.exe
CloseApplications=yes
RestartApplications=no
SetupLogging=yes
SetupIconFile=..\windows-controller\FactoryTimer.Controller\Assets\ESP32ControllerWindows.ico

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked
Name: "firewall"; Description: "Allow Factory Timer on private networks"; GroupDescription: "Network access:"; Flags: checkedonce

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PrereqDir}\VC_redist.x64.exe"; DestDir: "{tmp}"; Flags: ignoreversion deleteafterinstall

[Icons]
Name: "{autoprograms}\Factory Timer"; Filename: "{app}\FactoryTimer.Controller.exe"; WorkingDir: "{app}"; IconFilename: "{app}\FactoryTimer.Controller.exe"; IconIndex: 0
Name: "{autodesktop}\Factory Timer"; Filename: "{app}\FactoryTimer.Controller.exe"; WorkingDir: "{app}"; Tasks: desktopicon; IconFilename: "{app}\FactoryTimer.Controller.exe"; IconIndex: 0

[Run]
Filename: "{tmp}\VC_redist.x64.exe"; Parameters: "/install /quiet /norestart"; StatusMsg: "Installing Microsoft Visual C++ runtime..."; Flags: runhidden waituntilterminated
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""Factory Timer Controller"""; Flags: runhidden waituntilterminated; Tasks: firewall
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""Factory Timer Controller"" dir=in action=allow program=""{app}\FactoryTimer.Controller.exe"" enable=yes profile=private protocol=UDP"; Flags: runhidden waituntilterminated; Tasks: firewall
Filename: "{app}\FactoryTimer.Controller.exe"; Description: "Launch Factory Timer"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""Factory Timer Controller"""; Flags: runhidden waituntilterminated
