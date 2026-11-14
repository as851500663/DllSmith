; DllSmith 安装脚本（Inno Setup 7）
; 用法: ISCC.exe DllSmith.iss
; 1.4.0 变更（商用就绪 / 安全审计 A-11）：
;   · 版本号 1.4.0，与 Core.cs AppInfo.Version、Rebuild\src\AssemblyInfo.cs 三处一致
;   · 补齐 URL / Publisher / VersionInfo（原来只有占位 github.com/）
;   · 去掉 PrivilegesRequiredOverridesAllowed（原来允许降权装到用户可写目录，A-11）
;   · AppMutex 与程序内互斥名一致（4DDiG_DLLFixer_Rebuild_SingleInstance，原来对不上）
;   · 允许代码签名：ISCC /Ssign.ps1="..." 或在 SignTool 段接 scripts\sign.ps1

#define AppName "DllSmith"
#define AppNameCN "DLL 修复匠"
#define AppVersion "1.4.0"
#define AppPublisher "DllSmith Project"
#define AppURL "https://github.com/as851500663/DllSmith"
#define AppExeName "DllSmith.exe"
#define AppId "{{8F3C2B10-6A4D-4C21-9F51-2A7D0B5E9C31}"

[Setup]
AppId={#AppId}
AppName={#AppName} · {#AppNameCN}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}/issues
AppUpdatesURL={#AppURL}/releases
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
; 固定管理员安装到 Program Files：不允许降权装到用户可写目录（A-11）
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=6.1sp1
; 必须与 Program.cs 的 Mutex 名一致，否则安装/运行时互斥失效
AppMutex=4DDiG_DLLFixer_Rebuild_SingleInstance
VersionInfoVersion={#AppVersion}.0
VersionInfoCompany={#AppPublisher}
VersionInfoDescription={#AppName} 安装程序 · DLL 修复匠
VersionInfoProductName={#AppName} · {#AppNameCN}
VersionInfoProductVersion={#AppVersion}
VersionInfoCopyright=Copyright (C) 2026 {#AppPublisher} · GPL-3.0
VersionInfoTextVersion={#AppVersion}

[Languages]
Name: "chinese"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: checkedonce
Name: "quicklaunchicon"; Description: "创建快速启动图标"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\Rebuild\bin\{#AppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\Rebuild\bin\config.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\Rebuild\bin\Json\*"; DestDir: "{app}\Json"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\Rebuild\bin\7z\*"; DestDir: "{app}\7z"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "app.ico"; DestDir: "{app}"; Flags: ignoreversion
Source: "license.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "privacy.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"; IconFilename: "{app}\app.ico"
Name: "{group}\卸载 {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; IconFilename: "{app}\app.ico"; Tasks: desktopicon
Name: "{userappdata}\Microsoft\Internet Explorer\Quick Launch\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: quicklaunchicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}\Logs"
