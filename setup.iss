[Setup]
AppName=DevDeck
AppVersion=1.0.0.13
; По умолчанию предлагаем локальную папку, но даем ВЫБОР
DefaultDirName={localappdata}\DevDeck
DefaultGroupName=DevDeck
; Установка только для текущего пользователя (не нужен админ)
PrivilegesRequired=lowest

; --- ВКЛЮЧАЕМ ДИАЛОГОВЫЕ ОКНА ---
DisableDirPage=no
DisableWelcomePage=no
DisableProgramGroupPage=no
; Позволяет пользователю видеть процесс распаковки
AlwaysShowDirOnReadyPage=yes

OutputDir=installer_output
OutputBaseFilename=DevDeck_Setup
Compression=lzma2
SolidCompression=yes
SetupIconFile=icon.ico
UninstallDisplayIcon={app}\DevDeck.exe
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64


[Files]
Source: "publish-new\DevDeck.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "publish-new\icon.ico"; DestDir: "{app}"; Flags: ignoreversion
Source: "publish-new\wwwroot\*"; DestDir: "{app}\wwwroot"; Flags: ignoreversion recursesubdirs
Source: "publish-new\templates\*"; DestDir: "{app}\Templates"; Flags: ignoreversion recursesubdirs
Source: "publish-new\docs-vault\*"; DestDir: "{app}\docs-vault"; Flags: ignoreversion recursesubdirs
Source: "publish-new\appsettings.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "w:\code_hard\.net\z3n8.Tasks\ps1\*"; DestDir: "{app}\Tasks\ps1"; Flags: ignoreversion
Source: "w:\code_hard\.net\z3n8.Tasks\examples\*"; DestDir: "{app}\Tasks\examples"; Flags: ignoreversion
Source: "z3n7dll\*"; DestDir: "{app}\z3n7dll"; Flags: ignoreversion


[Icons]
Name: "{group}\DevDeck"; Filename: "{app}\DevDeck.exe"
Name: "{autodesktop}\DevDeck"; Filename: "{app}\DevDeck.exe"

[Run]
Filename: "netsh"; Parameters: "http add urlacl url=http://*:38109/ user=Everyone"; Flags: runhidden; StatusMsg: "Registering port 38109..."
Filename: "netsh"; Parameters: "http add urlacl url=http://*:38110/ user=Everyone"; Flags: runhidden; StatusMsg: "Registering port 38110..."
Filename: "{app}\DevDeck.exe"; Description: "Запустить DevDeck"; Flags: postinstall nowait skipifsilent