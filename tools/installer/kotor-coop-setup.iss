; The graphical Windows installer: kotor-coop-<version>-setup.exe. Built by tools\package.ps1
; (Inno Setup 6, ISCC.exe) from the same staged game-directory image the zip is made of:
;
;   ISCC.exe /DSourceDir=<staging> /DAppVersion=<version> /DNumericVersion=<a.b.c> /O<dist> kotor-coop-setup.iss
;
; It is a front end only. Every file still goes in through `KotorCoop.Agent.exe install`
; (PackageInstaller.cs, the one installer): the package is unpacked to {tmp}, and the agent
; there copies it into the chosen game folder - chaining another mod's dinput8.dll, writing
; coop.ini, the firewall rule and the install check exactly as the zip's double-click setup
; does. The wizard adds the two things a console cannot do well: a folder picker, and an
; options page whose answers go in as --windowed / --no-firewall / --set key=value.
; Uninstall runs `KotorCoop.Agent.exe uninstall` from a temp copy (the agent cannot delete
; itself), which keeps coop.ini just as the command-line uninstall does.

#ifndef SourceDir
  #error Pass /DSourceDir=<staged package folder> (tools\package.ps1 does).
#endif
#ifndef AppVersion
  #define AppVersion "dev"
#endif
#ifndef NumericVersion
  #define NumericVersion "0.0.0"
#endif
#ifndef IconFile
  #define IconFile AddBackslash(SourcePath) + "..\..\src\KotorCoop.Agent\Assets\icon.ico"
#endif

[Setup]
AppId={{6C7F2B0E-3D4A-4C55-9E2B-8A1F0C6D5B3A}
AppName=kotor-coop
AppVersion={#AppVersion}
AppVerName=kotor-coop {#AppVersion}
AppPublisher=kotor-coop
VersionInfoVersion={#NumericVersion}
VersionInfoProductTextVersion={#AppVersion}
VersionInfoDescription=kotor-coop setup (Star Wars: KOTOR co-op mod)
; The "install folder" is the game's own folder: nothing goes anywhere else.
DefaultDirName={code:DefaultGameDirectory}
AppendDefaultDirName=no
DirExistsWarning=no
UsePreviousAppDir=yes
DisableProgramGroupPage=yes
DisableReadyPage=no
; Game folders under Program Files (a default Steam library) need it, and so does the
; firewall rule for hosting. One UAC prompt, the same one the console setup asks for.
PrivilegesRequired=admin
; /CURRENTUSER skips the prompt for a folder the player can already write to (a GOG default
; under C:\GOG Games); the firewall rule is then printed rather than added.
PrivilegesRequiredOverridesAllowed=commandline
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; The agent is the only thing touching game files; nothing for Restart Manager to close.
CloseApplications=no
; Inno's own uninstaller lives in a subfolder so the game folder root only gains our files.
UninstallFilesDir={app}\kotorcoop-uninstall
UninstallDisplayName=kotor-coop (KOTOR co-op mod)
UninstallDisplayIcon={app}\KotorCoop.Agent.exe
SetupIconFile={#IconFile}
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
OutputBaseFilename=kotor-coop-{#AppVersion}-setup

[Messages]
WizardSelectDir=Choose your KOTOR folder
SelectDirDesc=Where is Star Wars: Knights of the Old Republic installed?
SelectDirLabel3=Setup will add kotor-coop to your KOTOR folder (the folder that contains swkotor.exe).
SelectDirBrowseLabel=Steam: right-click the game > Manage > Browse local files. GOG Galaxy: the game's settings (cog) > Manage installation > Show folder. Click Next to continue.

[Files]
; Unpacked to {tmp} only; the agent copies it into the game. Never registered for uninstall.
Source: "{#SourceDir}\*"; DestDir: "{tmp}\package"; Flags: recursesubdirs createallsubdirs ignoreversion

[Run]
Filename: "{app}\swkotor.exe"; WorkingDir: "{app}"; Description: "Launch KOTOR now"; Flags: postinstall nowait skipifsilent unchecked runasoriginaluser shellexec

[Code]
const
  GogKey = 'SOFTWARE\GOG.com\Games\1207666283';
  SteamUninstallKey = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 32370';

var
  OptionsPage, DefaultsPage: TWizardPage;
  NameEdit, PortEdit: TNewEdit;
  HelperWindowCheck, WindowedCheck, MenuButtonCheck, SteamCheck, FirewallCheck, DefaultsCheck: TNewCheckBox;
  OptionsNote, DefaultsNote: TNewStaticText;
  DefaultsMemo: TNewMemo;
  DefaultsPreview: String;
  DefaultsAvailable: Boolean;
  PrefilledFor: String;
  InstallFailed: Boolean;
  InstallOutput: String;

function IsGameFolder(const Dir: String): Boolean;
begin
  Result := (Dir <> '') and FileExists(AddBackslash(Dir) + 'swkotor.exe');
end;

function FirstGameFolder(const Candidates: array of String): String;
var
  I: Integer;
begin
  Result := '';
  for I := 0 to GetArrayLength(Candidates) - 1 do
    if IsGameFolder(Candidates[I]) then
    begin
      Result := RemoveBackslashUnlessRoot(Candidates[I]);
      Exit;
    end;
end;

{ The same places PackageInstaller.FindGameDirectories looks first: GOG (the primary target),
  then Steam's own record of where app 32370 lives, then the default folders. The agent
  re-checks whatever is chosen; this is only the folder the wizard opens on. }
function DefaultGameDirectory(Param: String): String;
var
  Gog, SteamApp, SteamRoot: String;
begin
  Gog := '';
  SteamApp := '';
  SteamRoot := '';
  if not RegQueryStringValue(HKLM32, GogKey, 'path', Gog) then
    RegQueryStringValue(HKLM64, GogKey, 'path', Gog);
  if not RegQueryStringValue(HKLM64, SteamUninstallKey, 'InstallLocation', SteamApp) then
    RegQueryStringValue(HKLM32, SteamUninstallKey, 'InstallLocation', SteamApp);
  if RegQueryStringValue(HKCU, 'Software\Valve\Steam', 'SteamPath', SteamRoot) then
  begin
    StringChangeEx(SteamRoot, '/', '\', True);
    SteamRoot := AddBackslash(SteamRoot) + 'steamapps\common\swkotor';
  end;

  Result := FirstGameFolder([Gog, SteamApp, SteamRoot,
    'C:\GOG Games\Star Wars - KotOR',
    ExpandConstant('{commonpf32}\Steam\steamapps\common\swkotor')]);
  if Result = '' then
    Result := 'C:\GOG Games\Star Wars - KotOR';
end;

{ coop.ini values are "key = value ; comment" in a [coop] section; GetIniString keeps the
  inline comment, so cut it here. }
function CoopSetting(const Key, Default: String): String;
var
  Cut: Integer;
begin
  Result := GetIniString('coop', Key, #1, AddBackslash(WizardDirValue) + 'coop.ini');
  if Result = #1 then
  begin
    Result := Default;
    Exit;
  end;
  Cut := Pos(';', Result);
  if (Pos('#', Result) > 0) and ((Cut = 0) or (Pos('#', Result) < Cut)) then
    Cut := Pos('#', Result);
  if Cut > 0 then
    Result := Copy(Result, 1, Cut - 1);
  Result := Trim(Result);
end;

function IsOn(const Value: String): Boolean;
var
  V: String;
begin
  V := Lowercase(Trim(Value));
  Result := not ((V = '0') or (V = 'false') or (V = 'no') or (V = 'off'));
end;

function GameIsWindowed: Boolean;
var
  Ini: String;
begin
  Ini := AddBackslash(WizardDirValue) + 'swkotor.ini';
  Result := (GetIniString('Graphics Options', 'FullScreen', '1', Ini) = '0')
    and (GetIniString('Graphics Options', 'AllowWindowedMode', '0', Ini) = '1');
end;

function AddCheck(const Caption: String; var Top: Integer): TNewCheckBox;
begin
  Result := TNewCheckBox.Create(OptionsPage);
  Result.Parent := OptionsPage.Surface;
  Result.Top := Top;
  Result.Width := OptionsPage.SurfaceWidth;
  Result.Height := ScaleY(17);
  Result.Caption := Caption;
  Top := Top + ScaleY(24);
end;

function AddEdit(const Caption: String; Width: Integer; var Top: Integer): TNewEdit;
var
  Lbl: TNewStaticText;
begin
  Lbl := TNewStaticText.Create(OptionsPage);
  Lbl.Parent := OptionsPage.Surface;
  Lbl.Top := Top + ScaleY(3);
  Lbl.Caption := Caption;
  Result := TNewEdit.Create(OptionsPage);
  Result.Parent := OptionsPage.Surface;
  Result.Top := Top;
  Result.Left := ScaleX(130);
  Result.Width := Width;
  Top := Top + ScaleY(30);
end;

procedure InitializeWizard;
var
  Top: Integer;
begin
  OptionsPage := CreateCustomPage(wpSelectDir, 'Co-op options',
    'Everything here is saved in coop.ini in the game folder and can be changed there later.');
  Top := 0;
  NameEdit := AddEdit('Your player name:', ScaleX(200), Top);
  PortEdit := AddEdit('Port for hosting:', ScaleX(70), Top);
  Top := Top + ScaleY(4);
  HelperWindowCheck := AddCheck('Open the co-op helper window minimised behind the game', Top);
  WindowedCheck := AddCheck('Run the game in a window instead of fullscreen', Top);
  MenuButtonCheck := AddCheck('Show the Co-op button on the main menu', Top);
  SteamCheck := AddCheck('Enable optional Steam friend joining', Top);
  FirewallCheck := AddCheck('Allow friends to connect to games you host (adds a Windows Firewall rule)', Top);

  OptionsNote := TNewStaticText.Create(OptionsPage);
  OptionsNote.Parent := OptionsPage.Surface;
  OptionsNote.Top := Top + ScaleY(6);
  OptionsNote.Width := OptionsPage.SurfaceWidth;
  OptionsNote.AutoSize := False;
  OptionsNote.WordWrap := True;
  OptionsNote.Height := ScaleY(40);

  DefaultsPage := CreateCustomPage(OptionsPage.ID, 'Review recommended settings',
    'Keep your current choices, or apply the changes listed below.');
  DefaultsNote := TNewStaticText.Create(DefaultsPage);
  DefaultsNote.Parent := DefaultsPage.Surface;
  DefaultsNote.Width := DefaultsPage.SurfaceWidth;
  DefaultsNote.Height := ScaleY(55);
  DefaultsNote.AutoSize := False;
  DefaultsNote.WordWrap := True;
  DefaultsNote.Caption := 'These settings match older defaults, but may be choices you made deliberately. '
    + 'Nothing changes unless you tick the box. Setup backs up coop.ini before applying these changes. '
    + 'The options you chose on the previous page take priority.';
  DefaultsMemo := TNewMemo.Create(DefaultsPage);
  DefaultsMemo.Parent := DefaultsPage.Surface;
  DefaultsMemo.Top := ScaleY(60);
  DefaultsMemo.Width := DefaultsPage.SurfaceWidth;
  DefaultsMemo.Height := DefaultsPage.SurfaceHeight - ScaleY(90);
  DefaultsMemo.ReadOnly := True;
  DefaultsMemo.ScrollBars := ssVertical;
  DefaultsMemo.WordWrap := True;
  DefaultsCheck := TNewCheckBox.Create(DefaultsPage);
  DefaultsCheck.Parent := DefaultsPage.Surface;
  DefaultsCheck.Top := DefaultsPage.SurfaceHeight - ScaleY(23);
  DefaultsCheck.Width := DefaultsPage.SurfaceWidth;
  DefaultsCheck.Height := ScaleY(20);
  DefaultsCheck.Caption := 'Apply the listed changes and keep a backup of my previous settings';
  DefaultsCheck.Checked := False;
end;

{ Read-only preview comes from the same agent migration rules that apply it. Never duplicate
  the historical-default table in Pascal. Only these two self-contained files are needed. }
procedure PreviewDefaults;
var
  ResultCode, I: Integer;
  Output: TExecOutput;
begin
  DefaultsPreview := '';
  DefaultsMemo.Text := '';
  DefaultsAvailable := False;
  DefaultsCheck.Checked := False;
  if not FileExists(AddBackslash(WizardDirValue) + 'coop.ini') then
    Exit;
  try
    ExtractTemporaryFile('KotorCoop.Agent.exe');
    ExtractTemporaryFile('KotorCoop.DEFAULTS.ini');
    { ExtractTemporaryFile uses the temporary root, regardless of [Files].DestDir.
      The full install later uses the separate package subdirectory. }
    if not ExecAndCaptureOutput(ExpandConstant('{tmp}\KotorCoop.Agent.exe'),
      'install --preview-defaults --game-path "' + WizardDirValue + '" --set menu_button=1',
      ExpandConstant('{tmp}'), SW_HIDE, ewWaitUntilTerminated, ResultCode, Output) then
      RaiseException('Could not start the settings check.');
    if ResultCode <> 0 then
      RaiseException('The settings check could not read this installation.');
    for I := 0 to GetArrayLength(Output.StdOut) - 1 do
      if Trim(Output.StdOut[I]) <> '' then
        DefaultsPreview := DefaultsPreview + Output.StdOut[I] + #13#10;
    DefaultsAvailable := DefaultsPreview <> '';
    DefaultsMemo.Text := DefaultsPreview;
    DefaultsCheck.Enabled := DefaultsAvailable;
  except
    DefaultsMemo.Text := 'The recommended settings could not be checked. Your current settings will be kept. '
      + 'You can run setup again later to review them.';
    DefaultsCheck.Enabled := False;
    Log('Settings preview unavailable: ' + GetExceptionMessage);
  end;
end;

{ Prefill from what the chosen folder already has, so an update keeps the player's choices.
  Redone only when the folder changes, so going Back and Next keeps edits made here. Also run
  before installing, for a /SILENT install that never shows the page. }
procedure PrefillOptions;
begin
  if PrefilledFor <> WizardDirValue then
  begin
    PrefilledFor := WizardDirValue;
    NameEdit.Text := CoopSetting('name', '');
    PortEdit.Text := CoopSetting('port', '27140');
    HelperWindowCheck.Checked := Lowercase(CoopSetting('agent_window', 'minimized')) <> 'hidden';
    WindowedCheck.Checked := GameIsWindowed;
    MenuButtonCheck.Checked := IsOn(CoopSetting('menu_button', '1'));
    { Presence is only a prerequisite, not proof that Steam invitations work. Never load a
      player-supplied DLL inside the elevated installer. }
    SteamCheck.Enabled := FileExists(AddBackslash(WizardDirValue) + 'steam_api64.dll');
    SteamCheck.Checked := SteamCheck.Enabled and IsOn(CoopSetting('steam_invites', '1'));
    if SteamCheck.Enabled then
    begin
      SteamCheck.Caption := 'Enable optional Steam friend joining';
      OptionsNote.Caption := 'The optional Steam component was found. Steam must also be running with KOTOR owned; '
        + 'finding the file does not verify invitations. Leave the player name blank for a generated name.';
    end
    else
    begin
      SteamCheck.Caption := 'Steam friend joining unavailable - use Connect by address';
      OptionsNote.Caption := 'The optional Steam component is not included in this package. '
        + 'Connect by address works with Steam and GOG copies. Your existing Steam preference is preserved.';
    end;
    FirewallCheck.Checked := True;
    PreviewDefaults;
  end;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  { An empty successful preview is skipped. A failed check stays visible with recovery text. }
  Result := (PageID = DefaultsPage.ID) and not DefaultsAvailable and (DefaultsMemo.Text = '');
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = OptionsPage.ID then
    PrefillOptions;
end;

function HasBadCharacter(const Value: String): Boolean;
begin
  Result := (Pos(';', Value) > 0) or (Pos('#', Value) > 0) or (Pos('"', Value) > 0);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Port: Integer;
begin
  Result := True;
  if (CurPageID = wpSelectDir) and not IsGameFolder(WizardDirValue) then
  begin
    MsgBox('There is no swkotor.exe in that folder.' + #13#10#13#10
      + 'Choose the folder KOTOR is installed in (the one that contains swkotor.exe).', mbError, MB_OK);
    Result := False;
  end
  else if CurPageID = OptionsPage.ID then
  begin
    Port := StrToIntDef(Trim(PortEdit.Text), -1);
    if (Port < 1) or (Port > 65535) then
    begin
      MsgBox('The port must be a number from 1 to 65535 (the default is 27140).', mbError, MB_OK);
      Result := False;
    end
    else if HasBadCharacter(NameEdit.Text) then
    begin
      MsgBox('The player name cannot contain ; # or ".', mbError, MB_OK);
      Result := False;
    end;
  end;
end;

function OnOff(Value: Boolean): String;
begin
  if Value then Result := '1' else Result := '0';
end;

function YesNo(Value: Boolean): String;
begin
  if Value then Result := 'yes' else Result := 'no';
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo,
  MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
var
  Name, SteamStatus: String;
begin
  Name := Trim(NameEdit.Text);
  if Name = '' then Name := '(generated)';
  if SteamCheck.Enabled then
    SteamStatus := YesNo(SteamCheck.Checked) + ' (requires working Steam integration)'
  else
    SteamStatus := 'unavailable; join by address (existing preference kept)';
  Result := 'Game folder:' + NewLine + Space + WizardDirValue + NewLine + NewLine
    + 'Co-op options:' + NewLine
    + Space + 'Player name: ' + Name + NewLine
    + Space + 'Hosting port: ' + Trim(PortEdit.Text) + NewLine
    + Space + 'Helper window minimised behind the game: ' + YesNo(HelperWindowCheck.Checked) + NewLine
    + Space + 'Game in a window: ' + YesNo(WindowedCheck.Checked) + NewLine
    + Space + 'Main-menu Co-op button: ' + YesNo(MenuButtonCheck.Checked) + NewLine
    + Space + 'Steam friends-list joins: ' + SteamStatus + NewLine
    + Space + 'Windows Firewall rule for hosting: ' + YesNo(FirewallCheck.Checked);
  if DefaultsAvailable and DefaultsCheck.Checked then
    Result := Result + NewLine + NewLine + 'Recommended changes (with settings backup):' + NewLine + DefaultsPreview
  else
    Result := Result + NewLine + 'Other existing settings: kept unchanged';
end;

function InstallParameters: String;
var
  HelperWindow: String;
begin
  if HelperWindowCheck.Checked then HelperWindow := 'minimized' else HelperWindow := 'hidden';
  Result := 'install --game-path "' + WizardDirValue + '"'
    + ' --set "name=' + Trim(NameEdit.Text) + '"'
    + ' --set port=' + Trim(PortEdit.Text)
    + ' --set agent_window=' + HelperWindow
    + ' --set menu_button=' + OnOff(MenuButtonCheck.Checked);
  if SteamCheck.Enabled then
    Result := Result + ' --set steam_invites=' + OnOff(SteamCheck.Checked);
  if DefaultsAvailable and DefaultsCheck.Checked then
    Result := Result + ' --update-defaults';
  if WindowedCheck.Checked then
    Result := Result + ' --windowed';
  if not FirewallCheck.Checked then
    Result := Result + ' --no-firewall';
end;

procedure RunAgentInstall;
var
  Agent, LogPath: String;
  ResultCode, I: Integer;
  Output: TExecOutput;
begin
  PrefillOptions;
  Agent := ExpandConstant('{tmp}\package\KotorCoop.Agent.exe');
  WizardForm.StatusLabel.Caption := 'Installing into the game folder and checking the game files (the first time can take a few minutes)...';
  WizardForm.ProgressGauge.Style := npbstMarquee;
  try
    if not ExecAndCaptureOutput(Agent, InstallParameters, ExpandConstant('{tmp}\package'), SW_HIDE,
      ewWaitUntilTerminated, ResultCode, Output) then
    begin
      InstallFailed := True;
      InstallOutput := 'Could not start KotorCoop.Agent.exe: ' + SysErrorMessage(ResultCode);
      Exit;
    end;
  finally
    WizardForm.ProgressGauge.Style := npbstNormal;
  end;

  InstallOutput := '';
  for I := 0 to GetArrayLength(Output.StdOut) - 1 do
    InstallOutput := InstallOutput + Output.StdOut[I] + #13#10;
  for I := 0 to GetArrayLength(Output.StdErr) - 1 do
    InstallOutput := InstallOutput + Output.StdErr[I] + #13#10;

  { Kept for a bug report either way; the folder is the one the installed agent logs beside. }
  LogPath := AddBackslash(WizardDirValue) + 'kotorcoop-setup.log';
  SaveStringToFile(LogPath, InstallOutput, False);
  Log('KotorCoop.Agent.exe ' + InstallParameters + ' -> exit ' + IntToStr(ResultCode));
  Log(InstallOutput);
  InstallFailed := ResultCode <> 0;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    RunAgentInstall;
    if InstallFailed then
      MsgBox('kotor-coop could not be installed completely. The install check said:' + #13#10#13#10
        + InstallOutput + #13#10
        + 'This is saved as kotorcoop-setup.log in the game folder. Make sure KOTOR is not running and run setup again.',
        mbError, MB_OK);
  end;
end;

{ ---- uninstall ------------------------------------------------------------------------- }

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Game, AgentCopy: String;
  ResultCode: Integer;
begin
  if CurUninstallStep <> usUninstall then
    Exit;

  Game := ExpandConstant('{app}');
  AgentCopy := ExpandConstant('{tmp}\KotorCoop.Agent.exe');
  if not FileExists(AddBackslash(Game) + 'KotorCoop.Agent.exe') then
    Exit;
  if not FileCopy(AddBackslash(Game) + 'KotorCoop.Agent.exe', AgentCopy, False) then
  begin
    MsgBox('Could not prepare the uninstall. Close KOTOR and try again.', mbError, MB_OK);
    Abort;
  end;

  if not Exec(AgentCopy, 'uninstall --game-path "' + Game + '"', ExpandConstant('{tmp}'), SW_HIDE,
    ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
    MsgBox('kotor-coop could not remove everything (is KOTOR still running?). You can run '
      + 'KotorCoop.Agent.exe uninstall from the extracted zip later.', mbError, MB_OK);

  DeleteFile(AddBackslash(Game) + 'kotorcoop-setup.log');
end;
