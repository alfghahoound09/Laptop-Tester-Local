#ifndef AppVersion
  #define AppVersion "1.0.1"
#endif
#ifndef FileVersion
  #define FileVersion "1.0.1.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\publish\win-x64-installer"
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif

[Setup]
AppId={{D8A4E6B1-3F7C-4A9E-9B2D-7C1E5F8A2401}
AppName=LaptopTester Open Edition
AppVersion={#AppVersion}
AppVerName=LaptopTester Open Edition v{#AppVersion}
AppPublisher=LaptopTester Open Edition
AppPublisherURL=https://github.com/alfghahoound09/Laptop-Tester-Local
AppSupportURL=https://github.com/alfghahoound09/Laptop-Tester-Local/issues
AppUpdatesURL=https://github.com/alfghahoound09/Laptop-Tester-Local/releases
VersionInfoVersion={#FileVersion}
DefaultDirName={autopf}\LaptopTester
DefaultGroupName=LaptopTester
DisableProgramGroupPage=yes
AllowNoIcons=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir={#OutputDir}
OutputBaseFilename=LaptopTester-Setup-{#AppVersion}-win-x64
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
UninstallDisplayIcon={app}\LaptopTester.exe

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\LaptopTester Open Edition"; Filename: "{app}\LaptopTester.exe"
Name: "{autodesktop}\LaptopTester Open Edition"; Filename: "{app}\LaptopTester.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\LaptopTester.exe"; Description: "{cm:LaunchProgram,LaptopTester Open Edition}"; Flags: nowait postinstall skipifsilent
