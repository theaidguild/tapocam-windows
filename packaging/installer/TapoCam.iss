; Compiled by build.ps1: ISCC /DAppVersion=<version> /DNumericVersion=<X.Y.Z> /DPublishDir=<publish> /DOutputDir=<dist>
; Relative paths are resolved from this script's folder.
#ifndef AppVersion
  #error Pass /DAppVersion=<version>
#endif

[Setup]
AppId={{6F0E2C3B-8A4D-4B7E-9C51-2D7A9E4F1B63}
AppName=TapoCam
AppVersion={#AppVersion}
AppVerName=TapoCam {#AppVersion}
AppPublisher=HPI SAIL
AppPublisherURL=https://github.com/hpi-sail/tapocam-windows
VersionInfoVersion={#NumericVersion}
DefaultDirName={autopf}\TapoCam
DefaultGroupName=TapoCam
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19044
SetupIconFile=..\..\src\TapoCam.App\Assets\Camera.ico
UninstallDisplayIcon={app}\TapoCam.App.exe
LicenseFile=..\..\LICENSE
OutputDir={#OutputDir}
OutputBaseFilename=TapoCam-{#AppVersion}-win-x64-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes

[Languages]
Name: "ptbr"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "ffmpeg"; Description: "Instalar o FFmpeg com o winget (necessário para gravar, tirar fotos e ouvir o áudio)"; Check: not FfmpegFound
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\TapoCam"; Filename: "{app}\TapoCam.App.exe"
Name: "{autodesktop}\TapoCam"; Filename: "{app}\TapoCam.App.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\TapoCam.App.exe"; Description: "{cm:LaunchProgram,TapoCam}"; Flags: nowait postinstall skipifsilent

[Code]
const
  WingetArgs = 'install --id Gyan.FFmpeg.Essentials --exact --source winget ' +
    '--accept-package-agreements --accept-source-agreements --disable-interactivity';
  WebView2Key = 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  WebView2BootstrapperUrl = 'https://go.microsoft.com/fwlink/p/?LinkId=2124703';
  WebView2DownloadPage = 'https://developer.microsoft.com/microsoft-edge/webview2/';

function WebView2InRegistry(RootKey: Integer): Boolean;
var
  Version: String;
begin
  Result := RegQueryStringValue(RootKey, WebView2Key, 'pv', Version) and
    (Version <> '') and (Version <> '0.0.0.0');
end;

function WebView2Found: Boolean;
begin
  Result := WebView2InRegistry(HKLM32) or WebView2InRegistry(HKCU);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
begin
  Result := '';
  if WebView2Found then
  begin
    Log('WebView2 Runtime is already installed.');
    Exit;
  end;

  WizardForm.PreparingLabel.Caption := 'Baixando e instalando o Microsoft Edge WebView2 Runtime...';
  WizardForm.PreparingLabel.Update;
  Log('WebView2 Runtime not found; downloading Microsoft bootstrapper.');
  try
    DownloadTemporaryFile(WebView2BootstrapperUrl, 'MicrosoftEdgeWebview2Setup.exe', '', nil);
  except
    Log('WebView2 bootstrapper download failed: ' + GetExceptionMessage);
    Result := 'Não foi possível baixar o WebView2 Runtime. Verifique a conexão com a internet ' +
      'ou instale-o manualmente e execute o instalador novamente:' + #13#10 + WebView2DownloadPage;
    Exit;
  end;

  if not Exec(ExpandConstant('{tmp}\MicrosoftEdgeWebview2Setup.exe'),
    '/silent /install', '', SW_HIDE, ewWaitUntilTerminated, Code) then
  begin
    Log(Format('Could not start WebView2 bootstrapper: %s (%d)', [SysErrorMessage(Code), Code]));
    Result := 'Não foi possível iniciar o instalador do WebView2 Runtime. ' +
      'Instale-o manualmente e execute o instalador novamente:' + #13#10 + WebView2DownloadPage;
    Exit;
  end;
  Log(Format('WebView2 bootstrapper exit code: %d', [Code]));
  if not WebView2Found then
  begin
    Log('WebView2 Runtime was not detected after installation.');
    if Code = 0 then
      Result := 'O instalador do WebView2 foi executado, mas o runtime não foi encontrado.'
    else
      Result := Format('Não foi possível instalar o WebView2 Runtime (código %d).', [Code]);
    Result := Result + #13#10 + 'Instale-o manualmente e execute o instalador novamente:' + #13#10 + WebView2DownloadPage;
  end
  else
    Log('WebView2 Runtime detected after installation.');
end;

function FfmpegInPackageRoot(const PackagesDirectory: String): Boolean;
var
  PackageRec, VersionRec: TFindRec;
  PackagePath, VersionPath: String;
begin
  Result := False;
  if not DirExists(PackagesDirectory) then
    Exit;
  if FindFirst(AddBackslash(PackagesDirectory) + 'Gyan.FFmpeg.Essentials_*', PackageRec) then
  try
    repeat
      if ((PackageRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0) and
         (PackageRec.Name <> '.') and (PackageRec.Name <> '..') then
      begin
        PackagePath := AddBackslash(PackagesDirectory) + PackageRec.Name;
        if FindFirst(AddBackslash(PackagePath) + '*', VersionRec) then
        try
          repeat
            if ((VersionRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0) and
               (VersionRec.Name <> '.') and (VersionRec.Name <> '..') then
            begin
              VersionPath := AddBackslash(PackagePath) + VersionRec.Name;
              if FileExists(AddBackslash(VersionPath) + 'bin\ffmpeg.exe') then
              begin
                Result := True;
                Exit;
              end;
            end;
          until not FindNext(VersionRec);
        finally
          FindClose(VersionRec);
        end;
      end;
    until not FindNext(PackageRec);
  finally
    FindClose(PackageRec);
  end;
end;

function FfmpegFound: Boolean;
begin
  Result := (FileSearch('ffmpeg.exe', GetEnv('PATH')) <> '') or
    FileExists(ExpandConstant('{localappdata}\Microsoft\WinGet\Links\ffmpeg.exe')) or
    FileExists(ExpandConstant('{commonpf64}\WinGet\Links\ffmpeg.exe')) or
    FfmpegInPackageRoot(ExpandConstant('{localappdata}\Microsoft\WinGet\Packages')) or
    FfmpegInPackageRoot(ExpandConstant('{commonpf64}\WinGet\Packages'));
end;

procedure InstallFfmpeg;
var
  Winget: String;
  Code: Integer;
begin
  Winget := FileSearch('winget.exe', GetEnv('PATH'));
  if Winget = '' then
    Winget := ExpandConstant('{localappdata}\Microsoft\WindowsApps\winget.exe');
  WizardForm.StatusLabel.Caption := 'Instalando o FFmpeg com o winget...';
  Log('Running: ' + Winget + ' ' + WingetArgs);
  if not Exec(Winget, WingetArgs, '', SW_SHOW, ewWaitUntilTerminated, Code) then
    Code := -1;
  Log(Format('winget exit code: %d', [Code]));
  if not FfmpegFound then
    SuppressibleMsgBox(Format('Não foi possível instalar o FFmpeg automaticamente (código %d).' + #13#10#13#10 +
      'O TapoCam foi instalado, mas gravações, fotos e áudio precisam do FFmpeg. ' +
      'Abra o PowerShell e execute:' + #13#10#13#10 + 'winget install Gyan.FFmpeg.Essentials', [Code]),
      mbError, MB_OK, IDOK);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('ffmpeg') then
    InstallFfmpeg;
end;
