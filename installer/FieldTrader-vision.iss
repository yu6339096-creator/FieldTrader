#define AppVersion "0.8.6"
[Setup]
AppId={{B3D2F7F0-45A8-49D6-9A73-44F0E80DE094}
AppName=战地行商 · 听声识物
AppPublisher=取名字真麻烦
AppVersion={#AppVersion}
AppVerName=战地行商 0.8.6 · 市场只读采集测试版
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
UninstallDisplayIcon={app}\FieldTrader.exe
CloseApplications=yes
RestartApplications=no
SetupLogging=yes
Uninstallable=not IsPortableTest
CreateUninstallRegKey=not IsPortableTest
[Languages]
Name: "chinesesimp"; MessagesFile: "ChineseSimplified.isl"
[Messages]
WelcomeLabel2=即将安装战地行商0.8.6市场只读采集测试版。%n%n已包含本地.NET、Python、14类YOLO11m原权重和OCR运行组件，无需另行下载或选择模型。%n%n本轮用于观察游戏中的YOLO定位与OCR字段识别。请人工切换游戏页面，程序仅截图与分析；真实交易和自动导航未启用。%n%n已有声音识别与语音资源保留，个人数据库独立保存。
FinishedLabel=安装完成。%n%n可使用“战地行商 · 市场只读采集”快捷方式进入实测界面。点击“连续只读采集”后，在4秒内切回游戏。%n%n首次模型启动需要几秒，结果和原图保存在个人数据目录。管理费与手续费分开处理，8%仅为未验证临时估算。
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
Filename: "{app}\FieldTraderLauncher.exe"; Parameters: "--market-readonly"; Description: "打开市场只读采集"; Flags: nowait postinstall skipifsilent
[Code]
function IsPortableTest: Boolean;
begin
  Result := ExpandConstant('{param:PORTABLE|0}') = '1';
end;
