; DllSmith 安装脚本（Inno Setup 7）
; 用法: ISCC.exe DllSmith.iss

#define AppName "DllSmith"
#define AppNameCN "DLL 修复匠"
#define AppVersion "1.0.1"
#define AppPublisher "DllSmith 开源项目"
#define AppExeName "DllSmith.exe"
#define AppId "{{8F3C2B10-6A4D-4C21-9F51-2A7D0B5E9C31}"

[Setup]
AppId={#AppId}
AppName={#AppName} · {#AppNameCN}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL=https://github.com/
AppSupportURL=https://github.com/
DefaultDirName={autopf}\DllSmith
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
LicenseFile=license.txt
OutputDir=..\dist
OutputBaseFilename=DllSmith-Setup-{#AppVersion}
SetupIconFile=app.ico
UninstallDisplayIcon={app}\{#AppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
WizardImageFile=wizard-big.bmp
WizardSmallImageFile=wizard-small.bmp
WizardImageStretch=no
WizardSizePercent=110
ShowLanguageDialog=no
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=6.1sp1
AppMutex=DllSmith_SingleInstance
VersionInfoVersion={#AppVersion}
VersionInfoCompany={#AppPublisher}
VersionInfoDescription={#AppName} 安装程序
VersionInfoProductName={#AppName}

[Languages]
Name: "chinese"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: checkedonce
Name: "quicklaunchicon"; Description: "创建快速启动图标"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\bin\{#AppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\bin\config.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\bin\Json\*"; DestDir: "{app}\Json"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\bin\7z\*"; DestDir: "{app}\7z"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "app.ico"; DestDir: "{app}"; Flags: ignoreversion
Source: "license.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"; IconFilename: "{app}\app.ico"
Name: "{group}\卸载 {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; IconFilename: "{app}\app.ico"; Tasks: desktopicon
Name: "{userappdata}\Microsoft\Internet Explorer\Quick Launch\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: quicklaunchicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}\Logs"
