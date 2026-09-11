#ifndef AppVersion
  #define AppVersion "2.0.4"
#endif
#ifndef BuildRoot
  #define BuildRoot ".."
#endif

[Setup]
AppId={{D91B7A0A-2789-4F0D-AEE4-8D671C539223}
AppName=타이핑튠 (TypingTune)
AppVersion={#AppVersion}
AppVerName=타이핑튠 (TypingTune) {#AppVersion}
AppPublisher=TypingTune contributors
DefaultDirName={localappdata}\Programs\TypingTune
DefaultGroupName=TypingTune
PrivilegesRequired=lowest
MinVersion=10.0
DisableProgramGroupPage=no
AllowNoIcons=yes
UsePreviousAppDir=yes
UsePreviousTasks=yes
UninstallDisplayName=타이핑튠 (TypingTune)
UninstallDisplayIcon={app}\TypingTune.exe
SetupIconFile={#BuildRoot}\dist\TypingTune.ico
OutputDir={#BuildRoot}\artifacts
OutputBaseFilename=TypingTune-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
DisableWelcomePage=no
CloseApplications=yes
CloseApplicationsFilter=TypingTune.exe
RestartApplications=no
VersionInfoVersion={#AppVersion}.0
VersionInfoDescription=TypingTune Windows Installer
VersionInfoProductName=TypingTune
VersionInfoProductVersion={#AppVersion}
Uninstallable=yes
CreateUninstallRegKey=yes

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"

[Tasks]
Name: "startup"; Description: "Windows 로그인 시 타이핑튠을 알림 영역에서 시작"; GroupDescription: "추가 설정:"; Flags: unchecked

[Files]
Source: "{#BuildRoot}\dist\TypingTune.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#BuildRoot}\dist\TypingTune.exe.config"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#BuildRoot}\dist\TypingTune.ico"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#BuildRoot}\README-ko.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#BuildRoot}\PRIVACY.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#BuildRoot}\LLM-JSON-GUIDE.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#BuildRoot}\LICENSE"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{userdesktop}\타이핑튠 (TypingTune)"; Filename: "{app}\TypingTune.exe"; WorkingDir: "{app}"; IconFilename: "{app}\TypingTune.ico"; Comment: "한글 입력 오류 진단 및 보정"; Check: CreateDesktopShortcut
Name: "{group}\타이핑튠 (TypingTune)"; Filename: "{app}\TypingTune.exe"; WorkingDir: "{app}"; IconFilename: "{app}\TypingTune.ico"; Comment: "한글 입력 오류 진단 및 보정"
Name: "{group}\타이핑튠 제거"; Filename: "{uninstallexe}"
Name: "{userstartup}\타이핑튠 (TypingTune)"; Filename: "{app}\TypingTune.exe"; Parameters: "--tray"; WorkingDir: "{app}"; IconFilename: "{app}\TypingTune.ico"; Tasks: startup

[Run]
Filename: "{app}\TypingTune.exe"; Description: "타이핑튠 진단 화면 열기"; Flags: nowait postinstall skipifsilent

[Code]
function CreateDesktopShortcut: Boolean;
var
  I: Integer;
begin
  Result := True;
  // A command-line-only override supports isolated installation validation.
  // Normal interactive installation always creates the requested desktop icon.
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), '/NODESKTOPICON') = 0 then
      Result := False;
end;

function InitializeSetup: Boolean;
begin
  Result := IsDotNetInstalled(net48, 0);
  if not Result then
    SuppressibleMsgBox('타이핑튠에는 .NET Framework 4.8 이상이 필요합니다.' + #13#10 +
      'Windows 업데이트 또는 Microsoft 공식 .NET Framework 설치 프로그램으로 설치한 뒤 다시 실행해 주세요.',
      mbCriticalError, MB_OK, IDOK);
end;

procedure StopInstalledApp;
var
  AppPath: String;
  ResultCode: Integer;
begin
  AppPath := ExpandConstant('{app}\TypingTune.exe');
  if FileExists(AppPath) then
  begin
    // The application only signals the exit event for this exact executable path.
    // It does not stop a portable copy or an application in another directory.
    if Exec(AppPath, '--exit-if-path "' + AppPath + '"', ExpandConstant('{app}'),
      SW_HIDE, ewWaitUntilTerminated, ResultCode) then
      Sleep(800);
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  StopInstalledApp;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    StopInstalledApp;
end;

// No recursive UninstallDelete rule: settings, recovery files and exported
// diagnostics outside the installed application files are deliberately retained.
