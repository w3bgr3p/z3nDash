[Setup]
AppName=z3nDash
AppVersion=1.0.1.1
; По умолчанию предлагаем локальную папку, но даем ВЫБОР
DefaultDirName={localappdata}\z3nDash
DefaultGroupName=z3nDash
; Установка только для текущего пользователя (не нужен админ)
PrivilegesRequired=admin

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
Source: "publish-new\z3nDash.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "publish-new\icon.ico"; DestDir: "{app}"; Flags: ignoreversion
Source: "publish-new\wwwroot\*"; DestDir: "{app}\wwwroot"; Flags: ignoreversion recursesubdirs
Source: "publish-new\templates\*"; DestDir: "{app}\Templates"; Flags: ignoreversion recursesubdirs
Source: "publish-new\docs-vault\*"; DestDir: "{app}\docs-vault"; Flags: ignoreversion recursesubdirs
Source: "publish-new\appsettings.json"; DestDir: "{app}"; Flags: ignoreversion



[Icons]
Name: "{group}\z3nDash"; Filename: "{app}\z3nDash.exe"
Name: "{autodesktop}\z3nDash"; Filename: "{app}\z3nDash.exe"

[Run]
Filename: "netsh"; Parameters: "http add urlacl url=http://*:33333/ user=Everyone"; Flags: runhidden; StatusMsg: "Registering port 33333..."
Filename: "netsh"; Parameters: "http add urlacl url=http://localhost:33334/ user=Everyone"; Flags: runhidden; StatusMsg: "Registering replay port 33334..."
Filename: "netsh"; Parameters: "http add urlacl url=http://*:38109/ user=Everyone"; Flags: runhidden; StatusMsg: "Registering port 38109..."
Filename: "netsh"; Parameters: "http add urlacl url=http://*:38110/ user=Everyone"; Flags: runhidden; StatusMsg: "Registering port 38110..."
Filename: "{app}\z3nDash.exe"; Description: "Запустить z3nDash"; WorkingDir: "{app}"; Flags: postinstall shellexec nowait skipifsilent
