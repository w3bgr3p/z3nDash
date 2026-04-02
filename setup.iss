[Setup]
AppName=z3nIO
AppVersion=1.0.0.11
; По умолчанию предлагаем локальную папку, но даем ВЫБОР
DefaultDirName={localappdata}\z3nIO
DefaultGroupName=z3nIO
; Установка только для текущего пользователя (не нужен админ)
PrivilegesRequired=lowest

; --- ВКЛЮЧАЕМ ДИАЛОГОВЫЕ ОКНА ---
DisableDirPage=no
DisableWelcomePage=no
DisableProgramGroupPage=no
; Позволяет пользователю видеть процесс распаковки
AlwaysShowDirOnReadyPage=yes

OutputDir=installer_output
OutputBaseFilename=z3n8_Setup
Compression=lzma2
SolidCompression=yes
SetupIconFile=icon.ico
UninstallDisplayIcon={app}\z3nIO.exe
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64


[Files]
Source: "publish-new\z3nIO.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "publish-new\icon.ico"; DestDir: "{app}"; Flags: ignoreversion
Source: "publish-new\wwwroot\*"; DestDir: "{app}\wwwroot"; Flags: ignoreversion recursesubdirs
Source: "publish-new\appsettings.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "Tasks\ps1\*"; DestDir: "{app}\Tasks\ps1"; Flags: ignoreversion
Source: "Tasks\examples\*"; DestDir: "{app}\Tasks\examples"; Flags: ignoreversion
Source: "z3n7dll\*"; DestDir: "{app}\z3n7dll"; Flags: ignoreversion


[Icons]
Name: "{group}\z3nIO"; Filename: "{app}\z3nIO.exe"
Name: "{autodesktop}\z3nIO"; Filename: "{app}\z3nIO.exe"

[Run]
Filename: "netsh"; Parameters: "http add urlacl url=http://*:38109/ user=Everyone"; Flags: runhidden; StatusMsg: "Registering port 38109..."
Filename: "netsh"; Parameters: "http add urlacl url=http://*:38110/ user=Everyone"; Flags: runhidden; StatusMsg: "Registering port 38110..."
Filename: "{app}\z3nIO.exe"; Description: "Запустить z3nIO"; Flags: postinstall nowait skipifsilent