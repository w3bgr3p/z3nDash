[Setup]
AppName=z3nDash
AppVersion=1.0.0
; По умолчанию предлагаем локальную папку, но даем ВЫБОР
DefaultDirName={localappdata}\z3nDash
DefaultGroupName=z3nDash
; Установка только для текущего пользователя (не нужен админ)
PrivilegesRequired=lowest

; --- ВКЛЮЧАЕМ ДИАЛОГОВЫЕ ОКНА ---
DisableDirPage=no
DisableWelcomePage=no
DisableProgramGroupPage=no
; Позволяет пользователю видеть процесс распаковки
AlwaysShowDirOnReadyPage=yes

OutputDir=installer_output
OutputBaseFilename=z3nDash_Setup
Compression=lzma2
SolidCompression=yes
SetupIconFile=icon.ico
UninstallDisplayIcon={app}\z3nDash.exe
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64


[Files]
; Publish is a folder now, not a single file: Roslyn needs assemblies on disk,
; otherwise Assembly.Location is empty and no csx or xml OwnCode compiles.
; publish-new must be cleaned before publishing - stale files would ship as is.
Source: "publish-new\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "appsettings.secrets.json,*.pdb"
Source: "icon.ico"; DestDir: "{app}"; Flags: ignoreversion
Source: "zpServer.zp"; DestDir: "{app}"; Flags: ignoreversion



[Icons]
Name: "{group}\z3nDash"; Filename: "{app}\z3nDash.exe"
Name: "{autodesktop}\z3nDash"; Filename: "{app}\z3nDash.exe"

[Run]
Filename: "{app}\z3nDash.exe"; Description: "Запустить z3nDash"; WorkingDir: "{app}"; Flags: postinstall shellexec nowait skipifsilent
