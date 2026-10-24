; 云存储管家 安装脚本（Inno Setup 6）
;
; 编译：ISCC.exe installer\云存储管家.iss
; 产物：dist\云存储管家-安装程序.exe
;
; 设计要点：
;   * 默认装到「当前用户」目录（%LOCALAPPDATA%\Programs），免管理员权限，
;     而且这个目录一定可写——配置、密钥、日志都放在安装目录里。
;   * 用户可以改成任意路径；装到 Program Files 这类受保护位置时，
;     程序会自动把配置改存到用户目录（应用侧有兜底，不会写不进去）。
;   * 卸载不删用户配置：settings.json 里有密钥，删了用户得重新填。

#define AppName "云存储管家"
#define AppExeName "云存储管家.exe"
#define AppPublisher "mypc1995"

; 版本号不在这儿手写：build.ps1 会从根目录 Directory.Build.props 里读出来，
; 用 /DMyAppVersion=1.2.0 传进来（那是版本号的唯一来源）。
; 下面的兜底值只在有人单独跑 ISCC 时用。
#ifndef MyAppVersion
  #define AppVersion "1.2.0"
#else
  #define AppVersion MyAppVersion
#endif

[Setup]
AppId={{3E9B1C7A-5D24-4F86-9B3E-7A1C8D5E2B40}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL=https://www.52pojie.cn/
AppSupportURL=https://www.52pojie.cn/
AppUpdatesURL=https://www.52pojie.cn/

DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
AllowNoIcons=yes

OutputDir=..\dist
OutputBaseFilename={#AppName}-安装程序
SetupIconFile=..\src\R2Manager\app.ico
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}

Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
WizardSizePercent=110
MinVersion=10.0

; 免安装单文件版需要 .NET 运行时，得自带，所以体积大，这里用 lzma2 压到 ~35MB
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

CloseApplications=yes
RestartApplications=no
ChangesEnvironment=no

VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#AppVersion}
VersionInfoCompany={#AppPublisher}
VersionInfoDescription={#AppName} 安装程序

[Languages]
Name: "chinese"; MessagesFile: "ChineseSimplified.isl"

[CustomMessages]
chinese.WelcomeLabel1=欢迎使用 [name/ver]
chinese.SelectDirLabel3=程序会安装到下面这个文件夹。[br]配置、密钥和日志都放在这个文件夹里，
chinese.SelectDirLabel3=如果这个文件夹不可写，程序会自动改用当前用户的目录存放配置。
chinese.FinishedHeadingLabel=[name] 已经安装完成

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务："; Flags: unchecked

[Files]
Source: "..\dist\{#AppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\dist\使用说明.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"; IconFilename: "{app}\{#AppExeName}"
Name: "{group}\卸载 {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; IconFilename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "立即启动 {#AppName}"; Flags: nowait postinstall skipifsilent

[Code]
procedure CurStepChanged(CurStep: TSetupStep);
var
  Dir: String;
begin
  if CurStep = ssPostInstall then
  begin
    if not WizardSilent then
    begin
      Dir := ExpandConstant('{app}');
      MsgBox('安装目录：' + Dir + #13#10 + #13#10 +
             '配置、密钥与日志默认保存在该目录内，' +
             '把整个目录拷走就能搬到另一台电脑。' + #13#10 +
             '（若该目录不可写，程序会自动改存到当前用户目录。）',
             mbInformation, MB_OK);
    end;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    if not UninstallSilent then
      MsgBox('已卸载程序。' + #13#10 + #13#10 +
             '配置（settings.json，含已保存的密钥）不会被删除，' +
             '如需彻底清除，请手动删除安装目录。',
             mbInformation, MB_OK);
  end;
end;
