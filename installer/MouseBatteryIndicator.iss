#ifndef AppVersion
  #error AppVersion must be supplied by build-installer.ps1.
#endif
#ifndef SourceRoot
  #define SourceRoot ".."
#endif
#ifndef ArtifactsDir
  #define ArtifactsDir "..\artifacts"
#endif

[Setup]
AppId={{43C99E74-72D4-4346-96A3-BA61DF9562D9}
AppName=マウスのバッテリー
AppVersion={#AppVersion}
AppVerName=マウスのバッテリー {#AppVersion}
AppPublisher=LaceCelestia
AppPublisherURL=https://github.com/LaceCelestia/mouse-battery-indicator
AppSupportURL=https://github.com/LaceCelestia/mouse-battery-indicator/issues
AppUpdatesURL=https://github.com/LaceCelestia/mouse-battery-indicator/releases
DefaultDirName={localappdata}\Programs\MouseBatteryIndicator
DefaultGroupName=マウスのバッテリー
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
MinVersion=10.0
SourceDir={#SourceRoot}
OutputDir={#ArtifactsDir}
OutputBaseFilename=MouseBatteryIndicator-v{#AppVersion}-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
LanguageDetectionMethod=none
ShowLanguageDialog=yes
UninstallDisplayIcon={app}\MouseBatteryIndicator.exe
AppMutex=Local\ViperV3BatteryIndicator.Instance
CloseApplications=no
RestartApplications=no

[Languages]
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
japanese.DesktopShortcut=デスクトップにショートカットを作成する
japanese.LaunchApp=マウスのバッテリーを起動する
japanese.DotNetRequired=このアプリには .NET Framework 4.8 以降が必要です。Microsoft の公式サイトからインストールした後、セットアップを実行してください。
english.DesktopShortcut=Create a desktop shortcut
english.LaunchApp=Launch Mouse Battery Indicator
english.DotNetRequired=This app requires .NET Framework 4.8 or later. Install it from Microsoft's official website, then run this setup again.

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopShortcut}"; Flags: unchecked

[Files]
Source: ".work\MouseBatteryIndicator.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "docs\はじめに.txt"; DestDir: "{app}\docs"; Flags: ignoreversion

[Icons]
Name: "{group}\マウスのバッテリー"; Filename: "{app}\MouseBatteryIndicator.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\マウスのバッテリー"; Filename: "{app}\MouseBatteryIndicator.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[InstallDelete]
Type: files; Name: "{app}\ViperV3BatteryIndicator.exe"

[Run]
Filename: "{app}\MouseBatteryIndicator.exe"; Parameters: "--background"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall skipifsilent

[Code]
function InitializeSetup: Boolean;
begin
  Result := IsDotNetInstalled(net48, 0);
  if not Result then
    SuppressibleMsgBox(CustomMessage('DotNetRequired'), mbCriticalError, MB_OK, IDOK);
end;

function MatchExecutableCommand(Command, ExpectedPath: String; var Arguments: String): Boolean;
var
  CommandPath, Suffix: String;
  ClosingQuote, PathLength: Integer;
begin
  Result := False;
  Arguments := '';
  Command := Trim(Command);
  if Command = '' then
    Exit;

  if Command[1] = '"' then
  begin
    Delete(Command, 1, 1);
    ClosingQuote := Pos('"', Command);
    if ClosingQuote = 0 then
      Exit;
    CommandPath := Copy(Command, 1, ClosingQuote - 1);
    if CompareText(CommandPath, ExpectedPath) <> 0 then
      Exit;
    Suffix := Copy(Command, ClosingQuote + 1, Length(Command));
  end
  else
  begin
    PathLength := Length(ExpectedPath);
    if CompareText(Copy(Command, 1, PathLength), ExpectedPath) <> 0 then
      Exit;
    Suffix := Copy(Command, PathLength + 1, Length(Command));
  end;

  if Suffix <> '' then
    if (Suffix[1] <> ' ') and (Suffix[1] <> #9) then
      Exit;
  Arguments := Trim(Suffix);
  Result := True;
end;

function MatchInstalledExecutableCommand(Command: String; var Arguments: String): Boolean;
begin
  Result := MatchExecutableCommand(Command,
    ExpandConstant('{app}\MouseBatteryIndicator.exe'), Arguments);
  if not Result then
    Result := MatchExecutableCommand(Command,
      ExpandConstant('{app}\ViperV3BatteryIndicator.exe'), Arguments);
end;

function PreserveInstalledStartupApproval(RootKey: Integer): Boolean;
var
  ApprovalSubKey: String;
  ApprovalData: AnsiString;
begin
  Result := True;
  ApprovalSubKey := 'Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run';
  if RegValueExists(RootKey, ApprovalSubKey, 'MouseBatteryIndicator') then
    Exit;
  if not RegValueExists(RootKey, ApprovalSubKey, 'ViperV3BatteryIndicator') then
    Exit;

  if not RegQueryBinaryValue(RootKey, ApprovalSubKey, 'ViperV3BatteryIndicator', ApprovalData) then
  begin
    Result := False;
    Log('Preserved the previous startup entry because its approval state could not be read.');
    Exit;
  end;

  Result := RegWriteBinaryValue(RootKey, ApprovalSubKey, 'MouseBatteryIndicator', ApprovalData);
  if Result then
    Log('Preserved the existing Windows startup approval state for MouseBatteryIndicator.')
  else
    Log('Preserved the previous startup entry because its approval state could not be copied.');
end;

procedure UpgradeInstalledStartupEntry(RootKey: Integer);
var
  LegacyCommand, CurrentCommand, Arguments, UpdatedCommand, LegacyArguments: String;
  LegacyIsInstalled, CurrentExists: Boolean;
begin
  LegacyIsInstalled := False;
  if RegQueryStringValue(RootKey, 'Software\Microsoft\Windows\CurrentVersion\Run',
      'ViperV3BatteryIndicator', LegacyCommand) then
    LegacyIsInstalled := MatchInstalledExecutableCommand(LegacyCommand, LegacyArguments);

  CurrentExists := RegValueExists(RootKey, 'Software\Microsoft\Windows\CurrentVersion\Run',
    'MouseBatteryIndicator');
  if CurrentExists then
  begin
    if not RegQueryStringValue(RootKey, 'Software\Microsoft\Windows\CurrentVersion\Run',
        'MouseBatteryIndicator', CurrentCommand) then
      Exit;
    if not MatchInstalledExecutableCommand(CurrentCommand, Arguments) then
    begin
      Log('Preserved startup entries because MouseBatteryIndicator refers to another executable.');
      Exit;
    end;
  end
  else
  begin
    if not LegacyIsInstalled then
      Exit;
    if not PreserveInstalledStartupApproval(RootKey) then
      Exit;
    Arguments := LegacyArguments;
  end;

  UpdatedCommand := '"' + ExpandConstant('{app}\MouseBatteryIndicator.exe') + '"';
  if Arguments <> '' then
    UpdatedCommand := UpdatedCommand + ' ' + Arguments;
  if RegWriteStringValue(RootKey, 'Software\Microsoft\Windows\CurrentVersion\Run',
      'MouseBatteryIndicator', UpdatedCommand) then
  begin
    Log('Updated the existing installed startup entry to MouseBatteryIndicator.');
    if LegacyIsInstalled then
      if not RegDeleteValue(RootKey, 'Software\Microsoft\Windows\CurrentVersion\Run',
          'ViperV3BatteryIndicator') then
        Log('Could not remove the previous installed startup entry.');
  end
  else
    Log('Could not update the existing installed startup entry.');
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    UpgradeInstalledStartupEntry(HKCU32);
    if IsWin64 then
      UpgradeInstalledStartupEntry(HKCU64);
  end;
end;

procedure RemoveInstalledStartupEntry(RootKey: Integer; ValueName: String);
var
  Command, Arguments: String;
begin
  Log(Format('Checking startup registry view: %d', [RootKey]));
  if RegQueryStringValue(RootKey, 'Software\Microsoft\Windows\CurrentVersion\Run',
      ValueName, Command) then
  begin
    if MatchInstalledExecutableCommand(Command, Arguments) then
    begin
      if RegDeleteValue(RootKey, 'Software\Microsoft\Windows\CurrentVersion\Run',
          ValueName) then
        Log('Removed startup entry for the installed executable.')
      else
        Log('Could not remove startup entry for the installed executable.');
    end
    else
      Log('Preserved startup entry for another executable.');
  end
  else
    Log('Startup entry was not found or could not be read.');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    Log('Cleaning up startup entries after uninstall.');
    RemoveInstalledStartupEntry(HKCU32, 'MouseBatteryIndicator');
    RemoveInstalledStartupEntry(HKCU32, 'ViperV3BatteryIndicator');
    if IsWin64 then
    begin
      RemoveInstalledStartupEntry(HKCU64, 'MouseBatteryIndicator');
      RemoveInstalledStartupEntry(HKCU64, 'ViperV3BatteryIndicator');
    end;
  end;
end;
