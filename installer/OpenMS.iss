; Open M&S 설치 프로그램 (Inno Setup 6) — scripts/build-installer.ps1 이 publish 뒤 부른다.
;
;   ISCC.exe /DAppVersion=1.2.3 /DSourceDir=..\publish\win-x64 OpenMS.iss   → installer\Output\OpenMS-Setup-1.2.3.exe
;
; - 사용자 단위 설치(%LOCALAPPDATA%\Programs\OpenMS) — 관리자 권한 · UAC 없이 앱이 스스로 업데이트할 수 있다.
; - 앱(self-contained win-x64) + 앱이 실행 폴더에서 찾는 데이터: config\(시나리오 · 모델 · 환경 · 공력 DB) · samples\ ·
;   tools\(FEA 메시 · CFD · 풍동 파이썬 도구 — 선택 기능, 가상환경 제외) · validation\public · validation\reports(신뢰도 · 하중 등 — 큰 폴더 제외).
; - 앱이 업데이트할 때는 /SILENT 로 실행한다: 마법사 없이 진행 막대만, 실행 중인 앱을 닫고 설치한 뒤 앱을 다시 띄운다.
; - 사용자 설정(%APPDATA%\MnS.Simulator\settings.json — 테마 · 장면 · 업데이트)은 설치 폴더 밖이라 업데이트 · 제거해도 남는다.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish\win-x64"
#endif
#define AppName "Open M&S"
#define AppExe "MnS.Simulator.exe"
#define Repo ".."

[Setup]
; AppId 는 바꾸지 않는다 — 바꾸면 업데이트가 아니라 다른 프로그램으로 깔린다
AppId={{B215AFC2-E86B-440B-B09B-368C93FF992A}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=ghlee0786
AppPublisherURL=https://github.com/ghlee0786/open-ms
AppUpdatesURL=https://github.com/ghlee0786/open-ms/releases
VersionInfoVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\OpenMS
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
UsePreviousAppDir=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=Output
OutputBaseFilename=OpenMS-Setup-{#AppVersion}
SetupIconFile={#Repo}\src\Sim.App\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
; 업데이트 때 실행 중인 앱을 닫는다(앱도 설치 프로그램을 띄운 뒤 스스로 끝난다)
CloseApplications=force
CloseApplicationsFilter={#AppExe}
RestartApplications=no

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"

[Tasks]
Name: "desktopicon"; Description: "바탕 화면에 바로 가기 만들기"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#Repo}\config\*"; DestDir: "{app}\config"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#Repo}\samples\*"; DestDir: "{app}\samples"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist
; 선택 폴더 — 공개 스냅숏처럼 없는 저장소에서도 컴파일되게 skipifsourcedoesntexist
Source: "{#Repo}\tools\*"; DestDir: "{app}\tools"; Excludes: ".venv,__pycache__,*.pyc,.pytest_cache"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist
Source: "{#Repo}\validation\public\*"; DestDir: "{app}\validation\public"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist
Source: "{#Repo}\validation\reports\*"; DestDir: "{app}\validation\reports"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
; skipifsilent 를 붙이지 않는다 — 앱이 /SILENT 로 업데이트한 뒤에도 다시 띄운다
; 체크 상자 글은 & 가 바로 가기 문자가 되므로 && 로(마법사 완료 페이지에 'Open MS' 로 보였다)
Filename: "{app}\{#AppExe}"; Description: "{#StringChange(AppName, "&", "&&")} 실행"; Flags: nowait postinstall

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/IM {#AppExe} /F"; Flags: runhidden; RunOnceId: "CloseApp"

[UninstallDelete]
; 앱이 실행 폴더 아래에 남기는 세션 로그 · 스모크 캡처(사용자가 저장한 다른 결과는 남긴다)
Type: filesandordirs; Name: "{app}\results\logs"
Type: filesandordirs; Name: "{app}\results\smoke"
Type: dirifempty; Name: "{app}\results"
Type: dirifempty; Name: "{app}"
