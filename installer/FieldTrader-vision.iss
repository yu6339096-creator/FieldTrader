#define AppVersion "0.8.6"
[Setup]
AppId={{B3D2F7F0-45A8-49D6-9A73-44F0E80DE094}
AppName=战地行商 · 听声识物
AppPublisher=取名字真麻烦
AppVersion={#AppVersion}
AppVerName=战地行商 0.8.6 · 自动买入与兑换测试版
DefaultDirName={localappdata}\Programs\FieldTrader
DefaultGroupName=战地行商
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#PackageOutput}
OutputBaseFilename=FieldTrader-0.8.6-Readonly-Setup
Compression=lzma2/fast
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\FieldTraderLauncher.exe
CloseApplications=yes
RestartApplications=no
SetupLogging=yes
Uninstallable=not IsPortableTest
CreateUninstallRegKey=not IsPortableTest
[Languages]
Name: "chinesesimp"; MessagesFile: "ChineseSimplified.isl"
[Messages]
WelcomeLabel2=即将安装战地行商0.8.6自动买入与兑换测试版。%n%n已包含本地运行组件、YOLO和OCR模型，无需另行下载。OCR门槛75%，支持从当前详情页执行真实单笔买入、补齐和兑换。%n%n启动器默认请求管理员权限；启动时请在系统提示中点击“是”。交易需要填写预算并主动开启，每轮结束重置状态，历史未完成记录不再拦截下一轮。%n%n已有声音识别与语音资源保留，个人数据库独立保存。
FinishedLabel=安装完成。%n%n启动时请确认Windows管理员权限提示。进入“单笔自动买入/兑换”，填写预算、勾选启用并开始，4秒内切回游戏目标详情页。%n%n全局急停快捷键以程序显示为准。每轮最多处理一笔；结果不明会结束本轮，下一次主动开始会重新识别和核价。截图及操作日志保存在个人数据目录。
[Tasks]
Name: "desktopicon"; Description: "创建市场采集与主程序桌面快捷方式"; GroupDescription: "快捷方式："
[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb,*.pyc,__pycache__"
[Icons]
Name: "{group}\战地行商 · 市场只读采集"; Filename: "{app}\FieldTraderLauncher.exe"; Parameters: "--market-readonly"; WorkingDir: "{app}"; Check: not IsPortableTest
Name: "{group}\战地行商 · 听声识物"; Filename: "{app}\FieldTraderLauncher.exe"; WorkingDir: "{app}"; Check: not IsPortableTest
Name: "{autodesktop}\战地行商 · 市场只读采集"; Filename: "{app}\FieldTraderLauncher.exe"; Parameters: "--market-readonly"; WorkingDir: "{app}"; Tasks: desktopicon; Check: not IsPortableTest
Name: "{autodesktop}\战地行商 · 听声识物"; Filename: "{app}\FieldTraderLauncher.exe"; WorkingDir: "{app}"; Tasks: desktopicon; Check: not IsPortableTest
[Run]
Filename: "{app}\FieldTraderLauncher.exe"; Parameters: "--market-readonly"; Description: "打开市场只读采集"; Flags: nowait postinstall skipifsilent shellexec
[Code]
function IsPortableTest: Boolean;
begin
  Result := ExpandConstant('{param:PORTABLE|0}') = '1';
end;
