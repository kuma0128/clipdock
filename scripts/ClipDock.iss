#ifndef PackageDirectory
  #error Build with scripts/build-installer.ps1.
#endif
#ifndef AppVersion
  #error AppVersion is required.
#endif
#ifndef InstallerOutput
  #error InstallerOutput is required.
#endif
#ifndef UnsignedDevelopment
  #define UnsignedDevelopment 0
#endif
#ifndef UnsignedRelease
  #define UnsignedRelease 0
#endif
#if Int(UnsignedDevelopment) && Int(UnsignedRelease)
  #error Choose only one unsigned build mode.
#endif

[Setup]
AppId={{7AB34D02-4DD5-40C0-980E-432F864B41D4}
AppName=ClipDock
AppVersion={#AppVersion}
AppPublisher=ClipDock
AppPublisherURL=https://clipdock-d6z.pages.dev/
AppSupportURL=https://clipdock-d6z.pages.dev/help
AppUpdatesURL=https://clipdock-d6z.pages.dev/
DefaultDirName={localappdata}\Programs\ClipDock
UsePreviousAppDir=no
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
SetupMutex=Local\ClipDock-Setup
CloseApplications=no
RestartApplications=no
WizardStyle=modern
UninstallDisplayIcon={app}\ClipDock.exe
UninstallDisplayName=ClipDock
UninstallLogMode=append
OutputDir={#InstallerOutput}
SetupIconFile=..\src\ClipDock.App\app.ico
Compression=lzma2
SolidCompression=yes
LicenseFile={#PackageDirectory}\LICENSE
VersionInfoVersion={#AppVersion}
#if Int(UnsignedDevelopment)
OutputBaseFilename=ClipDock-{#AppVersion}-win-x64-unsigned-dev-setup
SignedUninstaller=no
AppVerName=ClipDock {#AppVersion} (unsigned development build)
#elif Int(UnsignedRelease)
OutputBaseFilename=ClipDock-{#AppVersion}-win-x64-unsigned-setup
SignedUninstaller=no
#else
OutputBaseFilename=ClipDock-{#AppVersion}-win-x64-setup
SignTool=clipdock
SignedUninstaller=yes
#endif

[Languages]
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
japanese.CloseClipDock=ClipDockを終了してから、もう一度お試しください。通知領域のClipDockアイコンを右クリックし、「ClipDockを終了」を選びます。保存中の動画がある場合は、完了するまでお待ちください。
english.CloseClipDock=Quit ClipDock, then try again. Right-click the ClipDock icon in the notification area and choose Quit ClipDock. If a video is being saved, wait for it to finish first.
japanese.ProcessCheckFailed=ClipDockの実行状態を確認できませんでした。Windowsを再起動してから、もう一度お試しください。
english.ProcessCheckFailed=Could not check whether ClipDock is running. Restart Windows and try again.
japanese.InvalidInstallDirectory=ClipDockは、このユーザーの標準インストール先にインストールしてください。
english.InvalidInstallDirectory=ClipDock must be installed in this user's default application directory.
japanese.LaunchClipDock=ClipDockを起動する
english.LaunchClipDock=Launch ClipDock
japanese.DesktopIcon=デスクトップにショートカットを作成する
english.DesktopIcon=Create a desktop shortcut
japanese.UnsignedTitle=このインストーラーは未署名です
english.UnsignedTitle=This installer is unsigned
japanese.UnsignedDescription=Windowsに公開者やSmartScreenの警告が表示される場合があります。
english.UnsignedDescription=Windows may show an unknown publisher or SmartScreen warning.
japanese.UnsignedDetails=ClipDockにはコード署名証明書による署名がありません。公式サイトから取得したファイルであることと、公開されているSHA256が一致することを確認してから続行してください。
english.UnsignedDetails=ClipDock does not have a code-signing certificate signature. Before continuing, confirm that you downloaded it from the official site and that its SHA256 matches the published checksum.

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopIcon}"; Flags: unchecked

[Files]
Source: "{#PackageDirectory}\*"; DestDir: "{app}"; Excludes: "Install.ps1,Uninstall.ps1,INSTALL.cmd,UNINSTALL.cmd,.clipdock-package.json,*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\ClipDock"; Filename: "{app}\ClipDock.exe"; WorkingDir: "{app}"; Flags: uninsneveruninstall
Name: "{userdesktop}\ClipDock"; Filename: "{app}\ClipDock.exe"; WorkingDir: "{app}"; Tasks: desktopicon; Flags: uninsneveruninstall

[Run]
Filename: "{app}\ClipDock.exe"; WorkingDir: "{app}"; Description: "{cm:LaunchClipDock}"; Flags: nowait postinstall skipifsilent

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  NativeKey = 'Software\Google\Chrome\NativeMessagingHosts\com.clipdock.downloader';

#if Int(UnsignedRelease)
procedure InitializeWizard;
begin
  CreateOutputMsgPage(wpWelcome, CustomMessage('UnsignedTitle'),
    CustomMessage('UnsignedDescription'), CustomMessage('UnsignedDetails'));
end;
#endif

function RunningCheck: String;
var
  Locator, Services, Processes: Variant;
begin
  Result := '';
  try
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Services := Locator.ConnectServer('.', 'root\CIMV2');
    Processes := Services.ExecQuery('SELECT ProcessId FROM Win32_Process WHERE Name = ''ClipDock.exe'' OR Name = ''ClipDock.NativeHost.exe''');
    if Processes.Count > 0 then Result := CustomMessage('CloseClipDock');
  except
    Result := CustomMessage('ProcessCheckFailed');
    Log(GetExceptionMessage);
  end;
end;

function IsReparsePoint(const Path: String): Boolean;
var
  Found: TFindRec;
begin
  Result := False;
  if FindFirst(Path, Found) then begin
    try
      Result := (Found.Attributes and $400) <> 0;
    finally
      FindClose(Found);
    end;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  if CompareText(RemoveBackslashUnlessRoot(ExpandFileName(ExpandConstant('{app}'))),
    ExpandConstant('{localappdata}\Programs\ClipDock')) <> 0 then
    Result := CustomMessage('InvalidInstallDirectory')
  else if IsReparsePoint(ExpandConstant('{app}')) or IsReparsePoint(ExpandConstant('{app}\docs')) or
    IsReparsePoint(ExpandConstant('{app}\licenses')) or IsReparsePoint(ExpandConstant('{app}\extension')) or
    IsReparsePoint(ExpandConstant('{app}\extension\icons')) then
    Result := CustomMessage('InvalidInstallDirectory')
  else Result := RunningCheck;
end;

function InitializeUninstall: Boolean;
var
  Reason: String;
begin
  Reason := RunningCheck;
  Result := Reason = '';
  if not Result then SuppressibleMsgBox(Reason, mbError, MB_OK, IDOK);
end;

function ReadJsonString(const Text: String; var Position: Integer; var Value: String): Boolean;
var
  C: Char;
  Code: Integer;
begin
  Result := False;
  Value := '';
  if (Position > Length(Text)) or (Text[Position] <> '"') then Exit;
  Position := Position + 1;
  while Position <= Length(Text) do begin
    C := Text[Position];
    Position := Position + 1;
    if C = '"' then begin Result := True; Exit; end;
    if C = '\' then begin
      if Position > Length(Text) then Exit;
      C := Text[Position];
      Position := Position + 1;
      case C of
        '"', '\', '/': Value := Value + C;
        'b': Value := Value + #8;
        'f': Value := Value + #12;
        'n': Value := Value + #10;
        'r': Value := Value + #13;
        't': Value := Value + #9;
        'u': begin
          if Position + 3 > Length(Text) then Exit;
          Code := StrToIntDef('$' + Copy(Text, Position, 4), -1);
          if Code < 0 then Exit;
          Value := Value + Chr(Code);
          Position := Position + 4;
        end;
      else Exit;
      end;
    end else begin
      if Ord(C) < 32 then Exit;
      Value := Value + C;
    end;
  end;
end;

procedure SkipJsonWhitespace(const Text: String; var Position: Integer);
begin
  while Position <= Length(Text) do begin
    if (Text[Position] <> ' ') and (Text[Position] <> #9) and
      (Text[Position] <> #10) and (Text[Position] <> #13) then Exit;
    Position := Position + 1;
  end;
end;

function ManifestTargetsThisApp(const Path: String): Boolean;
var
  Lines: TArrayOfString;
  Text, Token, Target: String;
  I, Position, Depth: Integer;
  Found: Boolean;
begin
  Result := False;
  if not LoadStringsFromFile(Path, Lines) then Exit;
  Text := '';
  for I := 0 to GetArrayLength(Lines) - 1 do Text := Text + Lines[I] + #10;
  Position := 1;
  Depth := 0;
  Found := False;
  while Position <= Length(Text) do begin
    if Text[Position] = '"' then begin
      if not ReadJsonString(Text, Position, Token) then Exit;
      SkipJsonWhitespace(Text, Position);
      if (Depth = 1) and (Token = 'path') and (Position <= Length(Text)) then begin
        if Text[Position] = ':' then begin
          if Found then Exit;
          Position := Position + 1;
          SkipJsonWhitespace(Text, Position);
          if not ReadJsonString(Text, Position, Target) then Exit;
          Found := True;
        end;
      end;
    end else begin
      case Text[Position] of
        '{', '[': Depth := Depth + 1;
        '}', ']': Depth := Depth - 1;
      end;
      Position := Position + 1;
    end;
  end;
  if Found and (Depth = 0) and PathIsRooted(Target) then
    Result := CompareText(ExpandFileName(Target), ExpandConstant('{app}\ClipDock.NativeHost.exe')) = 0;
end;

procedure RemoveOwnedNativeRegistration;
var
  RegisteredPath, CanonicalPath: String;
begin
  CanonicalPath := ExpandConstant('{localappdata}\ClipDock\native-host.json');
  if RegQueryStringValue(HKCU, NativeKey, '', RegisteredPath) then begin
    if ManifestTargetsThisApp(RegisteredPath) then begin
      RegDeleteValue(HKCU, NativeKey, '');
      RegDeleteKeyIfEmpty(HKCU, NativeKey);
    end;
  end;
  if ManifestTargetsThisApp(CanonicalPath) then DeleteFile(CanonicalPath);
end;

procedure RemoveRetiredFiles;
var
  Files: TArrayOfString;
  I: Integer;
begin
  Files := ['ClipDock.NativeHost.exe', 'ClipDock.NativeHost.dll', 'ClipDock.NativeHost.deps.json',
    'ClipDock.NativeHost.runtimeconfig.json', 'ClipDock.NativeHost.pdb',
    'extension\background.js', 'extension\content.js', 'extension\distribution.js',
    'extension\manifest.json', 'extension\media.js', 'extension\popup.css', 'extension\popup.html',
    'extension\popup.js', 'extension\site-policy.js', 'extension\store-identity.json',
    'extension\icons\16.png', 'extension\icons\32.png', 'extension\icons\48.png',
    'extension\icons\128.png', 'extension\icons\256.png',
    'docs\webstore-listing-ja.md', 'docs\webstore-review.md'];
  for I := 0 to GetArrayLength(Files) - 1 do DeleteFile(ExpandConstant('{app}\') + Files[I]);
  RemoveDir(ExpandConstant('{app}\extension\icons'));
  RemoveDir(ExpandConstant('{app}\extension'));
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep <> ssDone then Exit;
  RemoveOwnedNativeRegistration;
  RemoveRetiredFiles;
  DeleteFile(ExpandConstant('{app}\Install.ps1'));
  DeleteFile(ExpandConstant('{app}\Uninstall.ps1'));
  DeleteFile(ExpandConstant('{app}\INSTALL.cmd'));
  DeleteFile(ExpandConstant('{app}\UNINSTALL.cmd'));
  DeleteFile(ExpandConstant('{app}\.clipdock-package.json'));
end;

procedure RemoveOwnedShortcut(const Path: String);
var
  Shell, Shortcut: Variant;
begin
  if not FileExists(Path) then Exit;
  try
    Shell := CreateOleObject('WScript.Shell');
    Shortcut := Shell.CreateShortcut(Path);
    if CompareText(Shortcut.TargetPath, ExpandConstant('{app}\ClipDock.exe')) = 0 then DeleteFile(Path);
  except
    Log('Retained shortcut: ' + GetExceptionMessage);
  end;
end;

// The app starts itself at sign-in with "<app>\ClipDock.exe" --background. Only that entry is removed.
procedure RemoveOwnedStartupEntry;
var
  Command, Target: String;
  Closing: Integer;
begin
  if not RegQueryStringValue(HKCU, RunKey, 'ClipDock', Command) then Exit;
  if (Length(Command) < 3) or (Command[1] <> '"') then Exit;
  Target := Copy(Command, 2, Length(Command) - 1);
  Closing := Pos('"', Target);
  if Closing < 2 then Exit;
  Target := Copy(Target, 1, Closing - 1);
  if CompareText(ExpandFileName(Target), ExpandConstant('{app}\ClipDock.exe')) = 0 then
    RegDeleteValue(HKCU, RunKey, 'ClipDock');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Reason: String;
begin
  if CurUninstallStep <> usUninstall then Exit;
  Reason := RunningCheck;
  if Reason <> '' then RaiseException(Reason);
  RemoveOwnedNativeRegistration;
  RemoveOwnedStartupEntry;
  RemoveOwnedShortcut(ExpandConstant('{userprograms}\ClipDock.lnk'));
  RemoveOwnedShortcut(ExpandConstant('{userdesktop}\ClipDock.lnk'));
end;
