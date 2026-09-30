; Windows installer (Inno Setup 6): TGK-win-x64-setup.exe.
; Installs for the current user only (no administrator rights) into %LOCALAPPDATA%\Programs\TGK, a folder the user
; can write to, so the app can update itself there ("Update now"). The folder can't be changed: uninstalling removes
; it as a whole (in-app updates add files the installer doesn't know), which must never hit a folder of the user's.
; The vault, settings and logs live in %APPDATA%\tgk and are kept.
;
; Build: ISCC /DAppVersion=<version> /DNumericVersion=<X.Y.Z> /DSourceDir=<publish folder> /DOutputDir=<dir> TGK.iss

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef NumericVersion
  #define NumericVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\..\publish\TGK"
#endif
#ifndef OutputDir
  #define OutputDir "..\..\dist"
#endif

[Setup]
; Never change AppId: Windows recognizes installed copies (for upgrades and uninstalling) by it.
AppId={{12FD58B9-75C2-497F-8177-9588AA8CA4F9}
AppName=TGK
AppVersion={#AppVersion}
AppVerName=TGK {#AppVersion}
VersionInfoVersion={#NumericVersion}
AppPublisher=Alexandru Mindra
AppPublisherURL=https://github.com/AlexandruMindra/TGK
AppSupportURL=https://github.com/AlexandruMindra/TGK/issues
AppUpdatesURL=https://github.com/AlexandruMindra/TGK/releases
DefaultDirName={localappdata}\Programs\TGK
DisableProgramGroupPage=yes
DisableDirPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename=TGK-win-x64-setup
SetupIconFile=..\..\assets\icon.ico
UninstallDisplayIcon={app}\TGK.exe
UninstallDisplayName=TGK
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\TGK"; Filename: "{app}\TGK.exe"
Name: "{autodesktop}\TGK"; Filename: "{app}\TGK.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\TGK.exe"; Description: "{cm:LaunchProgram,TGK}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Files added by in-app updates and their leftovers are not in the installer's list: remove the folder as a whole.
Type: filesandordirs; Name: "{app}"
