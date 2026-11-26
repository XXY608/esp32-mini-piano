#ifndef Payload
  #error Payload is required
#endif
[Setup]
AppId={{0D629C63-B42B-44F6-971A-B8E71AE8F721}
AppName=电子琴
AppVersion={#AppVersion}
AppVerName=电子琴 {#AppVersion}
DefaultDirName={localappdata}\Programs\HarmonicaPlayer
DefaultGroupName=电子琴
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#OutputDirectory}
OutputBaseFilename=电子琴安装程序
Compression=lzma2/normal
SolidCompression=yes
LZMANumBlockThreads=2
WizardStyle=modern
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\电子琴.exe
CloseApplications=no
RestartApplications=no
SetupLogging=yes
InfoBeforeFile=安装说明.txt

[Languages]
Name: "chinesesimp"; MessagesFile: "ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; Flags: unchecked

[Files]
Source: "{#Payload}\*"; DestDir: "{app}"; Excludes: "*.pyc"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\电子琴"; Filename: "{app}\电子琴.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\电子琴"; Filename: "{app}\电子琴.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\电子琴.exe"; Description: "启动电子琴"; Flags: nowait postinstall skipifsilent

[Code]
function PlayerRunning: Boolean;
var
  Locator, Services, Processes: Variant;
begin
  Locator := CreateOleObject('WbemScripting.SWbemLocator');
  Services := Locator.ConnectServer('', 'root\CIMV2');
  Processes := Services.ExecQuery('SELECT Name FROM Win32_Process WHERE Name = ''电子琴.exe'' OR Name = ''HarmonicaPlayer.exe''');
  Result := Processes.Count > 0;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if PlayerRunning then
    Result := '电子琴正在运行，请自行关闭后重试。安装程序不会结束正在运行的播放器。';
end;

function InitializeUninstall(): Boolean;
begin
  Result := not PlayerRunning;
  if not Result then
    MsgBox('电子琴正在运行，请自行关闭后重试。卸载程序不会结束播放器。', mbError, MB_OK);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    if DirExists(ExpandConstant('{app}\简谱')) then
      Log('保留用户简谱、歌词绑定、设置和处理记录。');
end;
