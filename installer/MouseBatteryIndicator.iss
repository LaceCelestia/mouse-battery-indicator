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
UninstallDisplayIcon={app}\ViperV3BatteryIndicator.exe
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
Source: ".work\ViperV3BatteryIndicator.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "docs\はじめに.txt"; DestDir: "{app}\docs"; Flags: ignoreversion

[Icons]
Name: "{group}\マウスのバッテリー"; Filename: "{app}\ViperV3BatteryIndicator.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\マウスのバッテリー"; Filename: "{app}\ViperV3BatteryIndicator.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\ViperV3BatteryIndicator.exe"; Parameters: "--background"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall skipifsilent

[Code]
function InitializeSetup: Boolean;
begin
  Result := IsDotNetInstalled(net48, 0);
  if not Result then
    SuppressibleMsgBox(CustomMessage('DotNetRequired'), mbCriticalError, MB_OK, IDOK);
end;

function CommandPointsToInstalledExe(Command: String): Boolean;
var
  ExpectedPath, CommandPath: String;
  ClosingQuote, PathLength: Integer;
begin
  Result := False;
  Command := Trim(Command);
  ExpectedPath := ExpandConstant('{app}\ViperV3BatteryIndicator.exe');
  Log('Startup cleanup expected executable: ' + ExpectedPath);
  if Command = '' then
    Exit;

  if Command[1] = '"' then
  begin
    Delete(Command, 1, 1);
    ClosingQuote := Pos('"', Command);
    if ClosingQuote = 0 then
      Exit;
    CommandPath := Copy(Command, 1, ClosingQuote - 1);
    Log('Startup cleanup command executable: ' + CommandPath);
    Result := CompareText(CommandPath, ExpectedPath) = 0;
  end
  else
  begin
    PathLength := Length(ExpectedPath);
    if CompareText(Copy(Command, 1, PathLength), ExpectedPath) <> 0 then
      Exit;
    if Length(Command) = PathLength then
      Result := True
    else if Length(Command) > PathLength then
      Result := (Command[PathLength + 1] = ' ') or (Command[PathLength + 1] = #9);
  end;
end;

procedure RemoveInstalledStartupEntry(RootKey: Integer);
var
  Command: String;
begin
  Log(Format('Checking startup registry view: %d', [RootKey]));
  if RegQueryStringValue(RootKey, 'Software\Microsoft\Windows\CurrentVersion\Run',
      'ViperV3BatteryIndicator', Command) then
  begin
    if CommandPointsToInstalledExe(Command) then
    begin
      if RegDeleteValue(RootKey, 'Software\Microsoft\Windows\CurrentVersion\Run',
          'ViperV3BatteryIndicator') then
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
    RemoveInstalledStartupEntry(HKCU32);
    if IsWin64 then
      RemoveInstalledStartupEntry(HKCU64);
  end;
end;
