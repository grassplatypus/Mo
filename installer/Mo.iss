; Inno Setup script for Mo. Built by scripts\Publish-Release.ps1, which passes the
; version and the publish directory in. Per-user by design: no admin, no UAC prompt.

#ifndef MoVersion
  #define MoVersion "0.0.0.0"
#endif
#ifndef MoSourceDir
  #define MoSourceDir "..\src\Mo\bin\x64\Release\publish"
#endif
#ifndef MoOutputDir
  #define MoOutputDir "..\artifacts"
#endif

[Setup]
AppId={{8F3B1A64-3C2E-4E6B-9D5A-1B0C7F2A9E41}
AppName=Mo
AppVersion={#MoVersion}
AppVerName=Mo {#MoVersion}
AppPublisher=GrassPlatypus
DefaultDirName={autopf}\Mo
DefaultGroupName=Mo
DisableProgramGroupPage=yes
UninstallDisplayName=Mo
UninstallDisplayIcon={app}\Mo.exe
SetupIconFile=..\src\Mo\Assets\AppIcon.ico
OutputDir={#MoOutputDir}
OutputBaseFilename=Mo-{#MoVersion}-Setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

; Per-user: {autopf} lands in %LOCALAPPDATA%\Programs, so nothing needs elevation.
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; Mo lives in the tray, so an upgrade has to close the running copy first. Restart
; Manager only sends WM_CLOSE, which Mo answers by hiding to the tray, so the [Code]
; section below asks it to exit properly. This stays as the fallback.
CloseApplications=yes
CloseApplicationsFilter=*.exe,*.dll
RestartApplications=no

; No AppMutex on purpose. Inno checks it before the wizard runs, so its own "please close
; all instances" message would appear before the [Code] below ever gets to offer to close
; Mo, which is the offer we actually want to make.

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"

; Defined here rather than relying on LaunchProgram and CreateDesktopIcon out of the
; bundled .isl files. A translation that is missing one resolves to an empty string, and
; a checkbox with no label reads as no checkbox at all.
[CustomMessages]
english.ClosingMo=Closing Mo...
korean.ClosingMo=Mo를 닫는 중...
english.MoRunningPrompt=Mo is running and has to close before this can continue.%n%nClose it now?
korean.MoRunningPrompt=Mo가 실행 중입니다. 계속하려면 Mo를 닫아야 합니다.%n%n지금 닫을까요?
english.MoStillRunning=Mo could not be closed. Close it yourself, then run this again.
korean.MoStillRunning=Mo를 닫지 못했습니다. 직접 종료한 뒤 다시 실행해 주세요.
english.LaunchMo=Run Mo now
korean.LaunchMo=지금 Mo 실행
english.DesktopIcon=Create a desktop shortcut
korean.DesktopIcon=바탕 화면에 바로 가기 만들기

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopIcon}"; Flags: unchecked

[Files]
Source: "{#MoSourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Mo"; Filename: "{app}\Mo.exe"
Name: "{userdesktop}\Mo"; Filename: "{app}\Mo.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Mo.exe"; Description: "{cm:LaunchMo}"; Flags: nowait postinstall skipifsilent unchecked

; Runs before the files are removed, so Mo can delete its own profiles, settings, logs
; and the auto-start entry. Without it an uninstall leaves %LOCALAPPDATA%\Mo behind.
[UninstallRun]
Filename: "{app}\Mo.exe"; Parameters: "--cleanup --quiet"; Flags: runhidden; RunOnceId: "MoDataCleanup"

[Code]
{ Asking Mo to exit, rather than closing its window. Names match
  src\Mo\Services\ShutdownSignal.cs; keep the two in step. }

const
  EVENT_MODIFY_STATE = $0002;
  SYNCHRONIZE = $00100000;
  QuitEventName = 'MoAppQuitRequest';
  RunningMutexName = 'MoAppRunning';
  { Every wait here blocks the wizard's own thread, so the window stops repainting while
    it runs. Kept short deliberately: a healthy Mo exits in well under a second, and the
    common upgrade path never waits at all because the old build has no event to signal. }
  QuitTimeoutMs = 3000;
  ForceTimeoutMs = 1000;
  QuitPollMs = 100;

function OpenEvent(dwDesiredAccess: LongWord; bInheritHandle: Boolean;
  lpName: string): THandle;
  external 'OpenEventW@kernel32.dll stdcall';
function SetEvent(hEvent: THandle): Boolean;
  external 'SetEvent@kernel32.dll stdcall';
function OpenMutex(dwDesiredAccess: LongWord; bInheritHandle: Boolean;
  lpName: string): THandle;
  external 'OpenMutexW@kernel32.dll stdcall';
function CloseHandle(hObject: THandle): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';

{ The presence mutex exists only in 0.22 and later. Its absence therefore proves nothing,
  which is why nothing below branches on "not running": every step is harmless when Mo is
  not there. Parsing tasklist output was the alternative and it is locale-dependent. }
function MoHoldsMutex(): Boolean;
var
  H: THandle;
begin
  H := OpenMutex(SYNCHRONIZE, False, RunningMutexName);
  Result := H <> 0;
  if Result then CloseHandle(H);
end;

{ The mutex only exists in 0.22 and later, so an older Mo has to be found by process
  name. Full paths because a machine can have something else called find.exe ahead of
  Windows' on PATH, and the image name is not localized. }
function MoIsRunning(): Boolean;
var
  Code: Integer;
begin
  if MoHoldsMutex() then
  begin
    Result := True;
    exit;
  end;

  Result := Exec(ExpandConstant('{cmd}'),
    ExpandConstant('/C "{sys}\tasklist.exe" /FI "IMAGENAME eq Mo.exe" /NH | "{sys}\find.exe" /I "Mo.exe" > nul'),
    '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 0);
end;

procedure WaitForMutexToClear(TimeoutMs: Integer);
var
  Waited: Integer;
begin
  Waited := 0;
  while (Waited < TimeoutMs) and MoHoldsMutex() do
  begin
    Sleep(QuitPollMs);
    Waited := Waited + QuitPollMs;
  end;
end;

procedure AskMoToQuit();
var
  H: THandle;
  Code: Integer;
begin
  { Say what the pause is for. The wizard cannot repaint during the waits below, so this
    has to be on screen before they start. Nil in the uninstaller, which has no wizard. }
  if WizardForm <> nil then
  begin
    WizardForm.StatusLabel.Caption := CustomMessage('ClosingMo');
    WizardForm.Refresh();
  end;

  { Polite first, and only 0.22 and later is listening. }
  H := OpenEvent(EVENT_MODIFY_STATE, False, QuitEventName);
  if H <> 0 then
  begin
    SetEvent(H);
    CloseHandle(H);
    WaitForMutexToClear(QuitTimeoutMs);
  end;

  { Blunt second. An older build has no listener and a wedged one has no window, so
    there is nothing to ask; taskkill reports nothing to do when Mo is not running.
    Profiles and settings are written when they change, so nothing is lost. }
  if MoHoldsMutex() or (H = 0) then
  begin
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM Mo.exe', '', SW_HIDE,
         ewWaitUntilTerminated, Code);
    WaitForMutexToClear(ForceTimeoutMs);
  end;
end;

{ Asks before closing anything. Returns False to stop, either because the user declined
  or because Mo outlasted both the polite request and the forced close. }
function ConfirmAndCloseMo(): Boolean;
begin
  Result := True;
  if not MoIsRunning() then exit;

  if MsgBox(CustomMessage('MoRunningPrompt'), mbConfirmation, MB_YESNO) <> IDYES then
  begin
    Result := False;
    exit;
  end;

  AskMoToQuit();

  if MoIsRunning() then
  begin
    MsgBox(CustomMessage('MoStillRunning'), mbError, MB_OK);
    Result := False;
  end;
end;

function InitializeSetup(): Boolean;
begin
  Result := ConfirmAndCloseMo();
end;

function InitializeUninstall(): Boolean;
begin
  Result := ConfirmAndCloseMo();
end;

{ Safety net for a Mo started while the wizard was open. Nothing to ask about here: the
  user already agreed to the install, and the files are about to be replaced. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  AskMoToQuit();
  Result := '';
end;

function PrepareToUninstall(): Boolean;
begin
  AskMoToQuit();
  Result := True;
end;
