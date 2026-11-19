#define AppVersion "0.1.4"
#define ShellClsid "9DF247FA-3897-4193-8F57-58062056862D"
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish"
#endif

[Setup]
AppId={{8E2C4C73-1712-46F1-956D-B919014935AD}
AppName=7Zipper
AppVersion={#AppVersion}
DefaultDirName={autopf}\7Zipper
DisableDirPage=no
DisableProgramGroupPage=yes
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=commandline
SetupArchitecture=x64
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.19041
SetupIconFile=..\assets\7zipper.ico
UninstallDisplayIcon={app}\7Zipper.exe
OutputDir=..\dist
OutputBaseFilename=7Zipper-Setup-{#AppVersion}-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ChangesAssociations=yes
CloseApplicationsFilter=7Zipper.exe,SevenZipper.Native.dll
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"

[Tasks]
Name: "contextmenu"; Description: "{cm:ContextMenuTask}"
Name: "openwith"; Description: "{cm:OpenWithTask}"; Flags: unchecked

[CustomMessages]
english.ContextMenuTask=Add 7Zipper to the File Explorer context menu
english.OpenWithTask=Offer 7Zipper in Open with for 7z and ZIP files
korean.ContextMenuTask=파일 탐색기 오른쪽 클릭 메뉴에 7Zipper 추가
korean.OpenWithTask=7z 및 ZIP 파일의 '연결 프로그램'에 7Zipper 추가

[Files]
Source: "{#PublishDir}\*"; Excludes: "SevenZipper.Shell.dll,7zipper.ico"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PublishDir}\SevenZipper.Shell.dll"; DestDir: "{app}"; Flags: replacesameversion restartreplace uninsrestartdelete
Source: "..\assets\7zipper.ico"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\7Zipper"; Filename: "{app}\7Zipper.exe"; WorkingDir: "{app}"

[Registry]
; Delete only our keys and values when optional integration is turned off on upgrade.
Root: HKA; Subkey: "Software\Classes\AllFilesystemObjects\shell\7Zipper"; Flags: deletekey; Tasks: not contextmenu
Root: HKA; Subkey: "Software\Classes\CLSID\{{{#ShellClsid}}"; Flags: deletekey; Tasks: not contextmenu
Root: HKA; Subkey: "Software\Classes\AllFilesystemObjects\shell\7Zipper"; ValueType: string; ValueName: "MUIVerb"; ValueData: "7Zipper"; Flags: uninsdeletekey; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\AllFilesystemObjects\shell\7Zipper"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\7Zipper.exe"",0"; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\AllFilesystemObjects\shell\7Zipper"; ValueType: string; ValueName: "ExplorerCommandHandler"; ValueData: "{{{#ShellClsid}}"; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\AllFilesystemObjects\shell\7Zipper"; ValueType: string; ValueName: "MultiSelectModel"; ValueData: "Player"; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\CLSID\{{{#ShellClsid}}"; ValueType: string; ValueData: "7Zipper shell commands"; Flags: uninsdeletekey; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\CLSID\{{{#ShellClsid}}\InprocServer32"; ValueType: string; ValueData: "{app}\SevenZipper.Shell.dll"; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\CLSID\{{{#ShellClsid}}\InprocServer32"; ValueType: string; ValueName: "ThreadingModel"; ValueData: "Apartment"; Tasks: contextmenu

Root: HKA; Subkey: "Software\Classes\7Zipper.Archive"; Flags: deletekey; Tasks: not openwith
Root: HKA; Subkey: "Software\Classes\.7z\OpenWithProgids"; ValueName: "7Zipper.Archive"; Flags: deletevalue; Tasks: not openwith
Root: HKA; Subkey: "Software\Classes\.zip\OpenWithProgids"; ValueName: "7Zipper.Archive"; Flags: deletevalue; Tasks: not openwith
Root: HKA; Subkey: "Software\Classes\7Zipper.Archive"; ValueType: string; ValueData: "7Zipper archive"; Flags: uninsdeletekey; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\7Zipper.Archive\DefaultIcon"; ValueType: string; ValueData: """{app}\7zipper.ico"",0"; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\7Zipper.Archive\shell\open\command"; ValueType: string; ValueData: """{app}\7Zipper.exe"" ""%1"""; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\.7z\OpenWithProgids"; ValueType: string; ValueName: "7Zipper.Archive"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\.zip\OpenWithProgids"; ValueType: string; ValueName: "7Zipper.Archive"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty; Tasks: openwith
