; ClipKeeper setup (Inno Setup 6). release.yml builds it next to the zip, from the same files:
;   ISCC /DAppVersion=1.2.0 /DAppNum=1.2.0.0 /DSourceDir=<the unpacked zip folder> /DOutputDir=dist installer\ClipKeeper.iss
; For one user, without admin rights: into %LOCALAPPDATA%\Programs\ClipKeeper. That folder is writable, so ClipKeeper keeps
; its data next to the exe and updates itself there, as the zip does; it also brings this setup's entry in "Apps" to the
; new version (Updates.SyncSetupEntry, with the AppId below).

#ifndef AppVersion
  #define AppVersion "0.0.0-dev"
#endif
#ifndef AppNum
  #define AppNum "0.0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\dist\ClipKeeper"
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif

[Setup]
; the same as Updates.SetupAppId — never change it, or updates install a second copy
AppId={{F0418725-5FD7-4201-B1E0-62BAE55CB01B}
AppName=ClipKeeper
AppVersion={#AppVersion}
AppVerName=ClipKeeper {#AppVersion}
AppPublisher=Alukkart
AppPublisherURL=https://github.com/Alukkart/ClipKeeper
AppSupportURL=https://github.com/Alukkart/ClipKeeper/issues
AppUpdatesURL=https://github.com/Alukkart/ClipKeeper/releases
VersionInfoVersion={#AppNum}
VersionInfoProductName=ClipKeeper
VersionInfoDescription=ClipKeeper setup
PrivilegesRequired=lowest
DefaultDirName={autopf}\ClipKeeper
DisableProgramGroupPage=yes
DisableReadyPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#OutputDir}
OutputBaseFilename=ClipKeeper-{#AppVersion}-setup
SetupIconFile=..\src\app.ico
UninstallDisplayIcon={app}\ClipKeeper.exe
UninstallDisplayName=ClipKeeper
WizardStyle=modern
; the logo on graphite (the finish page) and on white (the top right), at 100% and 200% scale
WizardImageFile=wizard.png,wizard-2x.png
WizardSmallImageFile=small.png,small-2x.png
Compression=lzma2/max
SolidCompression=yes
; a copy too old for "--exit" (see PrepareToInstall) is closed by Windows' Restart Manager
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"

[CustomMessages]
en.DeleteData=Also delete ClipKeeper's settings, device reference, covers, sounds, monthly recaps, OBS settings backups and log?%n%nYour clips stay where they are either way.
ru.DeleteData=Удалить и данные ClipKeeper — настройки, эталон устройств, обложки, звуки, итоги месяцев, копии настроек OBS и журнал?%n%nКлипы останутся на месте в любом случае.
en.StillRunning=ClipKeeper is still running. Exit it in the tray (right click → Exit) and try again.
ru.StillRunning=ClipKeeper всё ещё работает. Выйди из него в трее (правый клик → Выход) и попробуй снова.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\ClipKeeper.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\ffmpeg\*"; DestDir: "{app}\ffmpeg"; Flags: ignoreversion
Source: "{#SourceDir}\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\README.ru.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\CHANGELOG.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\LICENSE"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\ClipKeeper"; Filename: "{app}\ClipKeeper.exe"
Name: "{autodesktop}\ClipKeeper"; Filename: "{app}\ClipKeeper.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\ClipKeeper.exe"; Description: "{cm:LaunchProgram,ClipKeeper}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; what ClipKeeper's own updates leave next to the exe
Type: files; Name: "{app}\ClipKeeper.old.exe"
Type: files; Name: "{app}\ClipKeeper.new.exe"
Type: files; Name: "{app}\ClipKeeper.failed.exe"

[Code]
var
  WasRunning: Boolean;

// a ClipKeeper started from this folder — not an unpacked copy elsewhere, which this setup leaves alone.
// Only one copy runs at a time (Program.Main holds a mutex), so "--exit" reaches exactly this one
function ClipKeeperRunning: Boolean;
var
  Exe: String;
  Locator, Wmi, List: Variant;
begin
  Result := False;
  if not CheckForMutexes('OBSDeviceGuard') then Exit;
  Exe := ExpandConstant('{app}\ClipKeeper.exe');
  StringChangeEx(Exe, '\', '\\', True);
  StringChangeEx(Exe, '''', '\''', True);
  try
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Wmi := Locator.ConnectServer('.', 'root\CIMV2');
    List := Wmi.ExecQuery('SELECT ProcessId FROM Win32_Process WHERE ExecutablePath = ''' + Exe + '''');
    Result := List.Count > 0;
  except
    Result := True;   // can't tell: ask whichever copy runs to exit
  end;
end;

// the running copy exits the way "Exit" in the tray does; the new exe asks, so this works whatever version is running
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
begin
  Result := '';
  WasRunning := ClipKeeperRunning;
  if WasRunning then
  begin
    ExtractTemporaryFile('ClipKeeper.exe');
    Exec(ExpandConstant('{tmp}\ClipKeeper.exe'), '--exit', '', SW_HIDE, ewWaitUntilTerminated, Code);
  end;
end;

// a silent update (winget) has no "Launch ClipKeeper" box: the copy that was running starts again, to the tray
procedure CurStepChanged(CurStep: TSetupStep);
var
  Code: Integer;
begin
  if (CurStep = ssPostInstall) and WasRunning and WizardSilent then
    Exec(ExpandConstant('{app}\ClipKeeper.exe'), '--tray', '', SW_SHOW, ewNoWait, Code);
end;

function InitializeUninstall: Boolean;
var
  Code: Integer;
begin
  Result := True;
  if ClipKeeperRunning then
  begin
    Exec(ExpandConstant('{app}\ClipKeeper.exe'), '--exit', '', SW_HIDE, ewWaitUntilTerminated, Code);
    if ClipKeeperRunning then
    begin
      SuppressibleMsgBox(CustomMessage('StillRunning'), mbError, MB_OK, IDOK);
      Result := False;
    end;
  end;
end;

// only ClipKeeper's own files: the folder may have been typed by hand and hold something else
procedure DeleteData(Dir: String);
var
  Names: TStringList;
  I: Integer;
begin
  Names := TStringList.Create;
  try
    Names.CommaText := 'settings.json,devices.json,favorites.json,reviewed.json,clipstats.json,clipcache.json,' +
                       'update.json,update-failed.json,ClipKeeper.log,DeviceGuard.log,cleanup.log,cleanup-preview.txt,selftest.txt';
    for I := 0 to Names.Count - 1 do
      DeleteFile(Dir + '\' + Names[I]);
    Names.CommaText := 'covers,previews,backups,recap,sounds';
    for I := 0 to Names.Count - 1 do
      DelTree(Dir + '\' + Names[I], True, True, True);
  finally
    Names.Free;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Dir, Cmd: String;
begin
  if CurUninstallStep <> usPostUninstall then Exit;
  Dir := ExpandConstant('{app}');
  // autostart into the removed folder would start nothing
  if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'ClipKeeper', Cmd) and
     (Pos(Lowercase(Dir + '\'), Lowercase(Cmd)) > 0) then
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'ClipKeeper');
  if DirExists(Dir) and not UninstallSilent and
     (MsgBox(CustomMessage('DeleteData'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES) then
  begin
    DeleteData(Dir);
    RemoveDir(Dir);   // only if nothing else is left
  end;
end;
