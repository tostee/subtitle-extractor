<#
.SYNOPSIS
  Compila el instalador de Subtítulos: pruebas → publicación autocontenida (win-x64, no necesita .NET instalado) → Inno Setup.

.EXAMPLE
  cd app
  .\installer\build-installer.ps1              # versión de Directory.Build.props
  .\installer\build-installer.ps1 -SkipTests   # sin ejecutar las pruebas
  # Resultado: installer\Output\Subtitulos-Setup-<versión>.exe
#>
param(
    [string]$Version,
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$appRoot = Split-Path -Parent $PSScriptRoot
$publish = Join-Path $PSScriptRoot 'publish'

function Invoke-Checked([string]$what, [scriptblock]$block) {
    Write-Host "==> $what" -ForegroundColor Cyan
    # Las herramientas escriben avisos en stderr: se juzga solo por el código de salida.
    $ErrorActionPreference = 'Continue'
    & $block
    if ($LASTEXITCODE -ne 0) { throw "Falló: $what (código $LASTEXITCODE)" }
}

if (-not $Version) {
    [xml]$props = Get-Content (Join-Path $appRoot 'Directory.Build.props')
    $Version = @($props.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ })[0]
    if (-not $Version) { throw 'No hay <Version> en Directory.Build.props; usa -Version.' }
}

# Inno Setup 6 (instalación por usuario o para todos).
$iscc = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    $cmd = Get-Command iscc.exe -ErrorAction SilentlyContinue
    if ($cmd) { $iscc = $cmd.Source }
}
if (-not $iscc) { throw 'No se encontró Inno Setup 6 (ISCC.exe). Instálalo con: winget install JRSoftware.InnoSetup' }

if (-not $SkipTests) {
    Invoke-Checked 'Pruebas' { dotnet test (Join-Path $appRoot 'SubtitleTool.sln') -c Release --nologo }
}

if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }

# Ventana y consola en la misma carpeta (comparten el mismo runtime de .NET 8).
foreach ($project in @('src\SubtitleTool.App\SubtitleTool.App.csproj', 'src\SubtitleTool.Cli\SubtitleTool.Cli.csproj')) {
    Invoke-Checked "Publicando $project" {
        dotnet publish (Join-Path $appRoot $project) -c Release -r win-x64 --self-contained true --nologo `
            -p:Version=$Version -p:SatelliteResourceLanguages=es -o $publish
    }
}

Invoke-Checked "Inno Setup $Version" { & $iscc /Q "/DAppVersion=$Version" (Join-Path $PSScriptRoot 'Subtitulos.iss') }

$setup = Join-Path $PSScriptRoot "Output\Subtitulos-Setup-$Version.exe"
# Huella SHA-256 (formato sha256sum): la app la comprueba antes de instalar una actualización.
$hash = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLowerInvariant()
$checksum = "$setup.sha256"
[IO.File]::WriteAllText($checksum, "$hash  $(Split-Path $setup -Leaf)`n", (New-Object Text.UTF8Encoding($false)))
Write-Host ("Instalador listo: {0} ({1:N1} MB)" -f $setup, ((Get-Item $setup).Length / 1MB)) -ForegroundColor Green
Write-Host "SHA-256: $hash  ($checksum)"
