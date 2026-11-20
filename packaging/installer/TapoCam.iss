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
