; DSH-Sharp Windows 安装脚本（Inno Setup 6.3+）
; 构建：ISCC dshsharp.iss /DAppVersion=<版本> /DPayloadDir=<publish 输出目录> /DOutputDir=<Setup.exe 输出目录>
; 设计：每用户安装（免 UAC）、中文向导（语言文件随仓库分发）、桌面图标可选、开始菜单、
;       控制面板可卸载；升级直接覆盖安装，%APPDATA%\DSHSharp 用户数据（会话/运行时/设置）不受影响。

#define AppName "DSH-Sharp"
#ifndef AppVersion
#define AppVersion "0.0.0"
#endif
#ifndef PayloadDir
#define PayloadDir "..\..\artifacts\installer-payload\win-x64"
#endif
#ifndef OutputDir
#define OutputDir "..\..\artifacts\installer"
#endif

[Setup]
; 稳定 AppId：升级与卸载按此识别同一应用，勿改。
AppId={{A5A40096-247D-48C3-99BE-6BEF96B133F1}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=fengyang86
AppPublisherURL=https://github.com/fengyang86/dsh-sharp
AppUpdatesURL=https://github.com/fengyang86/dsh-sharp/releases
; 每用户安装：写入 LocalAppData，全程不触发 UAC。
DefaultDirName={localappdata}\Programs\{#AppName}
PrivilegesRequired=lowest
DisableProgramGroupPage=yes
; 安装/卸载时若客户端在运行，先提示关闭（单实例互斥锁）；静默模式下强制结束以免挂起。
AppMutex=DSHSharp.SingleInstance
CloseApplications=force
WizardStyle=modern
; 64 位 payload，按 64 位安装目录规则展开。
ArchitecturesInstallIn64BitMode=x64compatible
Compression=lzma2/max
SolidCompression=yes
OutputDir={#OutputDir}
OutputBaseFilename=DSHSharp-Setup-{#AppVersion}
SetupIconFile=..\..\src\DSHSharp\Assets\avalonia-logo.ico
UninstallDisplayName={#AppName} — DeepSeek Harness 桌面客户端
UninstallDisplayIcon={app}\DSHSharp.exe

[Languages]
; 第一个为默认语言：简体中文向导（语言文件随本仓库分发，不依赖安装器的内置集）。
Name: "chinesesimplified"; MessagesFile: "ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
chinesesimplified.DataNote=卸载不会删除会话数据（%USERPROFILE%\AppData\Roaming\DSHSharp），如需彻底清理请手动删除该目录。
english.DataNote=Uninstalling keeps session data (%USERPROFILE%\AppData\Roaming\DSHSharp). Delete that folder manually for a full cleanup.

[Registry]
; dshsharp:// 深链协议（每用户注册，随卸载清理）：dshsharp://session/<id> 直达会话。
Root: HKA; Subkey: "Software\Classes\dshsharp"; ValueType: string; ValueName: ""; ValueData: "URL:DSH-Sharp 会话深链"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\dshsharp"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\dshsharp\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\DSHSharp.exe,0"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\dshsharp\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\DSHSharp.exe"" ""%1"""; Flags: uninsdeletekey

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: checkedonce

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\DSHSharp.exe"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\DSHSharp.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\DSHSharp.exe"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; 兜底：卸载末尾若客户端仍存活（提示关闭被忽略），静默结束进程以便删净文件。
Filename: "{cmd}"; Parameters: "/C taskkill /IM DSHSharp.exe /F"; Flags: runhidden; RunOnceId: "KillApp"

[UninstallDelete]
; 应用运行时在安装目录生成的 WebView2 数据目录，卸载时一并清理。
Type: filesandordirs; Name: "{app}\DSHSharp.exe.WebView2"
Type: files; Name: "{app}\*.log"

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep = usPostUninstall) and not UninstallSilent then
    MsgBox(ExpandConstant('{cm:DataNote}'), mbInformation, MB_OK);
end;
