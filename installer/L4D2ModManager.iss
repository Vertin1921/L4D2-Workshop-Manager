; ============================================================================
;  L4D2 Mod Manager —— Inno Setup 6 安装脚本（可选方案）
;
;  说明：
;    项目自带的 src\L4D2ModManager.Setup 已能直接生成 Setup.exe，无需任何第三方工具。
;    如果你更习惯 Inno Setup，可以用本脚本生成同等的安装包：
;
;      1) 先运行 build\build-release.ps1（至少生成 artifacts\L4D2ModManager-win-x64）
;      2) 用 Inno Setup 6 编译本脚本：  iscc installer\L4D2ModManager.iss
;      3) 输出： artifacts\L4D2ModManager-Setup-Inno.exe
; ============================================================================

#define AppName "Left 4 Dead 2 Mod Manager"
#define AppShortName "L4D2 Mod Manager"
#define AppVersion "1.0.0"
#define AppPublisher "L4D2 Mod Manager Project"
#define AppExeName "L4D2ModManager.exe"

[Setup]
AppId={{7C4C1B52-6E1F-4F0A-9E77-1B4B0F2C550A}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppShortName}
DefaultGroupName={#AppShortName}
DisableProgramGroupPage=yes
OutputDir=..\artifacts
OutputBaseFilename=L4D2ModManager-Setup-Inno
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

[Languages]
Name: "chinese"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务："; Flags: checkedonce

[Files]
; 主程序文件（由 build\build-release.ps1 生成的便携版目录）
Source: "..\artifacts\L4D2ModManager-win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppShortName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\卸载 {#AppShortName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppShortName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "立即启动 {#AppShortName}"; Flags: nowait postinstall skipifsilent
