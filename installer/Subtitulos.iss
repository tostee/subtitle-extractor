; Instalador de Subtítulos (Inno Setup 6). Se compila con build-installer.ps1, que primero publica la app en .\publish.
; Se instala siempre en C:\Program Files\Subtitulos para todos los usuarios (pide permiso de administrador).

#define AppName "Subtítulos"
#define AppExe "Subtitulos.exe"
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

[Setup]
AppId={{6E4B1F0A-3C2D-4B7E-9A51-5D8C2F7E1A40}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
VersionInfoVersion={#AppVersion}
DefaultDirName={commonpf64}\Subtitulos
DisableDirPage=yes
UsePreviousAppDir=no
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir=Output
OutputBaseFilename=Subtitulos-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#AppExe}
SetupIconFile=..\src\SubtitleTool.App\Assets\Subtitulos.ico
WizardSmallImageFile=wizard-small-55.png,wizard-small-64.png,wizard-small-110.png,wizard-small-128.png
UninstallDisplayName={#AppName}
; Para que el cambio del PATH (subtool) llegue a las ventanas nuevas sin cerrar sesión.
ChangesEnvironment=yes
CloseApplications=yes

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

[Messages]
spanish.FinishedLabel=Se instaló [name] en este equipo.%n%nPara traducir con tu suscripción hace falta Claude Code (https://claude.com/claude-code) con la sesión iniciada. En la aplicación, «Ajustes… → Comprobar» revisa MKVToolNix, FFmpeg y Claude Code.

[Tasks]
Name: "desktopicon"; Description: "Crear un acceso directo en el escritorio"; GroupDescription: "Accesos directos:"
Name: "addtopath"; Description: "Agregar la herramienta de consola «subtool» al PATH"; GroupDescription: "Consola:"
Name: "installmkv"; Description: "Instalar MKVToolNix con winget (necesario para los .mkv; no se encontró)"; GroupDescription: "Herramientas que faltan:"; Check: NeedsMkvToolNix
Name: "installffmpeg"; Description: "Instalar FFmpeg con winget (solo para los .mp4; no se encontró)"; GroupDescription: "Herramientas que faltan:"; Check: NeedsFfmpeg

[Files]
Source: "publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{cmd}"; Parameters: "/C winget install --id MoritzBunkus.MKVToolNix -e --accept-source-agreements --accept-package-agreements"; StatusMsg: "Instalando MKVToolNix con winget…"; Tasks: installmkv; Flags: waituntilterminated
Filename: "{cmd}"; Parameters: "/C winget install --id Gyan.FFmpeg -e --accept-source-agreements --accept-package-agreements"; StatusMsg: "Instalando FFmpeg con winget…"; Tasks: installffmpeg; Flags: waituntilterminated
Filename: "{app}\{#AppExe}"; Description: "Abrir {#AppName}"; Flags: nowait postinstall skipifsilent
; Actualización desde la propia app (/UPDATE=1, en modo silencioso): se vuelve a abrir sola.
; runasoriginaluser: la app no debe quedar abierta como administrador (no admitiría arrastrar archivos desde el Explorador).
Filename: "{app}\{#AppExe}"; Flags: nowait runasoriginaluser; Check: IsAppUpdate

; Los ajustes, la clave cifrada, el progreso y los registros (%LOCALAPPDATA%\SubtitleTool) se conservan al desinstalar.

[Code]
const
  UserEnvKey = 'Environment';
  MachineEnvKey = 'SYSTEM\CurrentControlSet\Control\Session Manager\Environment';

function EnvRoot: Integer;
begin
  if IsAdminInstallMode then Result := HKEY_LOCAL_MACHINE else Result := HKEY_CURRENT_USER;
end;

function EnvKey: String;
begin
  if IsAdminInstallMode then Result := MachineEnvKey else Result := UserEnvKey;
end;

function WingetAvailable: Boolean;
begin
  Result := FileExists(ExpandConstant('{localappdata}\Microsoft\WindowsApps\winget.exe'))
    or (FileSearch('winget.exe', GetEnv('PATH')) <> '');
end;

function MkvToolNixFound: Boolean;
begin
  Result := FileExists(ExpandConstant('{commonpf64}\MKVToolNix\mkvmerge.exe'))
    or FileExists(ExpandConstant('{commonpf32}\MKVToolNix\mkvmerge.exe'))
    or (FileSearch('mkvmerge.exe', GetEnv('PATH')) <> '');
end;

function FfmpegFound: Boolean;
var
  FindRec: TFindRec;
begin
  Result := (FileSearch('ffprobe.exe', GetEnv('PATH')) <> '')
    or FileExists('C:\ffmpeg\bin\ffprobe.exe')
    or FileExists(ExpandConstant('{localappdata}\Microsoft\WinGet\Links\ffprobe.exe'));
  if not Result then
  begin
    if FindFirst(ExpandConstant('{localappdata}\Microsoft\WinGet\Packages\Gyan.FFmpeg*'), FindRec) then
    begin
      Result := True;
      FindClose(FindRec);
    end;
  end;
end;

function IsAppUpdate: Boolean;
begin
  Result := ExpandConstant('{param:UPDATE|0}') = '1';
end;

function NeedsMkvToolNix: Boolean;
begin
  Result := (not MkvToolNixFound) and WingetAvailable;
end;

function NeedsFfmpeg: Boolean;
begin
  Result := (not FfmpegFound) and WingetAvailable;
end;

procedure AddToPath(Dir: String);
var
  Paths: String;
begin
  if not RegQueryStringValue(EnvRoot, EnvKey, 'Path', Paths) then Paths := '';
  if Pos(';' + Uppercase(Dir) + ';', ';' + Uppercase(Paths) + ';') > 0 then Exit;
  if (Paths <> '') and (Paths[Length(Paths)] <> ';') then Paths := Paths + ';';
  RegWriteExpandStringValue(EnvRoot, EnvKey, 'Path', Paths + Dir);
end;

procedure RemoveFromPath(Dir: String);
var
  Paths: String;
  P: Integer;
begin
  if not RegQueryStringValue(EnvRoot, EnvKey, 'Path', Paths) then Exit;
  { P apunta al ';' que precede a Dir dentro de ';' + Paths + ';', es decir, a la posición de Dir en Paths menos uno. }
  P := Pos(';' + Uppercase(Dir) + ';', ';' + Uppercase(Paths) + ';');
  if P = 0 then Exit;
  if P > 1 then
    Delete(Paths, P - 1, Length(Dir) + 1)  { quita ';' + Dir }
  else
    Delete(Paths, 1, Length(Dir) + 1);     { era el primero: quita Dir + ';' }
  RegWriteExpandStringValue(EnvRoot, EnvKey, 'Path', Paths);
end;

{ Las versiones 1.0.0 y 1.0.1 se instalaban por usuario (en %LOCALAPPDATA%\Programs). Antes de instalar en
  Program Files se desinstala esa copia en silencio para no dejar dos (los ajustes y el avance se conservan). }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Uninstaller: String;
  ResultCode: Integer;
begin
  Result := '';
  if RegQueryStringValue(HKEY_CURRENT_USER,
       'Software\Microsoft\Windows\CurrentVersion\Uninstall\{6E4B1F0A-3C2D-4B7E-9A51-5D8C2F7E1A40}_is1', 'UninstallString', Uninstaller) then
  begin
    Uninstaller := RemoveQuotes(Uninstaller);
    if FileExists(Uninstaller) then
      Exec(Uninstaller, '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('addtopath') then
    AddToPath(ExpandConstant('{app}'));
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    RemoveFromPath(ExpandConstant('{app}'));
end;
