# subtool — extractor, traductor y validador de subtítulos

Extrae los subtítulos en inglés (pistas de texto) de archivos `.mkv` (con MKVToolNix) y `.mp4`/`.m4v` (con FFmpeg), los traduce al
español latinoamericano neutro con Claude —por defecto a través de **Claude Code** con tu suscripción, o con la API de
Anthropic— y valida traducciones `.srt` contra el original.

## Instalar

Descarga `Subtitulos-Setup-<versión>.exe` de la [última versión](https://github.com/tostee/subtitle-extractor/releases/latest) y ejecútalo. Se instala en `C:\Program Files\Subtitulos` para todos los usuarios (Windows pide permiso de
administrador) y **no necesita .NET instalado**: la app va autocontenida. Si tenías la 1.0.0 o la 1.0.1 (que se
instalaban en tu perfil), se quita esa copia automáticamente.

- Crea el acceso en el menú Inicio (y, si lo marcas, en el escritorio).
- Opción «Agregar `subtool` al PATH» para usar la consola desde cualquier carpeta (se quita al desinstalar).
- Si no encuentra MKVToolNix o FFmpeg, ofrece instalarlos con `winget` (MKVToolNix: `MoritzBunkus.MKVToolNix`;
  FFmpeg: `Gyan.FFmpeg`).
- Claude Code no se instala desde aquí: instálalo desde https://claude.com/claude-code e inicia sesión. En la app,
  «Ajustes… → Comprobar» revisa todo.
- Al desinstalar se conservan tus ajustes, la clave cifrada, el progreso y los registros (`%LOCALAPPDATA%\SubtitleTool`).

### Actualizaciones

Al abrirse, la app consulta la última Release de este repositorio (se puede desactivar en «Ajustes → Actualizaciones»;
también hay «Buscar actualizaciones» en la barra de estado y `subtool update` en la consola). Si hay una versión nueva
aparece un aviso con «Novedades» y «Actualizar ahora»: descarga el instalador, comprueba su huella SHA-256 (archivo
`.sha256` publicado junto a él), lo ejecuta en modo silencioso (Windows pide permiso de administrador) y vuelve a abrir la
app con tu usuario normal. Ajustes, clave, glosarios y avance se conservan. Sin conexión no molesta: solo lo anota en
el registro.

### Publicar una versión

1. Sube `<Version>` en `Directory.Build.props` y añade su sección `## [X.Y.Z] - fecha` en `CHANGELOG.md`.
2. Haz commit y sube la etiqueta: `git tag vX.Y.Z` y `git push origin main vX.Y.Z`.
3. El workflow `.github/workflows/release.yml` comprueba que la etiqueta coincide con la versión, ejecuta las pruebas,
   compila el instalador y crea la Release con `Subtitulos-Setup-X.Y.Z.exe` y su `.sha256`. Las apps instaladas la verán
   al abrirse.

Para generar el instalador en local (necesita [Inno Setup 6](https://jrsoftware.org/isinfo.php): `winget install JRSoftware.InnoSetup`):

```powershell
.\installer\build-installer.ps1             # pruebas → publicación win-x64 autocontenida → Inno Setup
.\installer\build-installer.ps1 -SkipTests  # sin pruebas
# → installer\Output\Subtitulos-Setup-1.0.0.exe y su .sha256  (versión: <Version> de Directory.Build.props)
```

## Requisitos

- Para compilar: .NET SDK 8 (el `global.json` fija 8.0.417 con `rollForward: latestFeature`). Para usar el instalador no hace falta.
- [MKVToolNix](https://mkvtoolnix.download/) (`mkvmerge.exe` y `mkvextract.exe`). Se buscan en:
  1. la carpeta indicada en la variable de entorno `SUBTOOL_MKVTOOLNIX`,
  2. `C:\Program Files\MKVToolNix`,
  3. `C:\Program Files (x86)\MKVToolNix`,
  4. el `PATH`.
- Solo para `.mp4`/`.m4v`: [FFmpeg](https://ffmpeg.org/download.html) (`ffmpeg.exe` y `ffprobe.exe`;
  `winget install Gyan.FFmpeg`). Se buscan en: la carpeta de Ajustes o la variable `SUBTOOL_FFMPEG` (vale la carpeta raíz
  o su `bin`), `C:\ffmpeg\bin`, `C:\Program Files\ffmpeg\bin`, las carpetas de winget, Chocolatey y Scoop, el `PATH` del
  proceso y el `PATH` guardado en el registro (por si se instaló con la app abierta). Sin FFmpeg, cada `.mp4` aparece con
  error y el resto de videos se procesan con normalidad.
- Para traducir (backend por defecto `claude-code`): [Claude Code](https://claude.com/claude-code) instalado
  (`claude` en el `PATH` o en `%USERPROFILE%\.local\bin`) y con sesión iniciada con tu suscripción
  (ejecuta `claude` y usa `/login`). Para `--backend api`: una clave de API de Anthropic.
- `subtool doctor` comprueba todo lo anterior.

## Compilar y probar

```powershell
dotnet build
dotnet test
```

El ejecutable queda en `src\SubtitleTool.Cli\bin\Debug\net8.0\subtool.exe`
(o ejecútalo con `dotnet run --project src\SubtitleTool.Cli -- <comando> ...`).

## Interfaz gráfica (Fase 3): `Subtitulos.exe`

```powershell
dotnet build
.\src\SubtitleTool.App\bin\Debug\net8.0-windows\Subtitulos.exe                 # ventana vacía
.\src\SubtitleTool.App\bin\Debug\net8.0-windows\Subtitulos.exe "D:\Peliculas"  # ya con una carpeta (o .mkv) cargada
# o: dotnet run --project src\SubtitleTool.App -- "D:\Peliculas"
```

Usa la misma librería (`SubtitleTool.Core`) que la CLI, así que el resultado es idéntico a `subtool run`.

- **Agregar videos**: «Agregar carpeta…», «Agregar archivos…», arrastrar y soltar carpetas o videos (`.mkv`, `.mp4`, `.m4v`)
  sobre la ventana, o pasarlos como argumentos. Cada video se analiza en segundo plano (`mkvmerge -J` o `ffprobe`) sin
  congelar la ventana. Al abrirse, el panel «Registro» indica dónde encontró MKVToolNix y FFmpeg (o si faltan).
- **Lista**: nombre, pista inglesa elegida (id y normal/SDH/forzada, «solo imagen — necesita OCR» o «sin inglés»),
  estado del inglés y del español (✅ existe / pendiente / en curso / ❌), progreso por video (tramo i/n, bloques) y detalle.
- **Procesar todo**: cola **secuencial** (un video cada vez, para no agotar el límite de la suscripción): extrae el inglés
  si falta → traduce si no hay `.es.srt` → actualiza la fila. Al terminar se muestra un resumen ✅/⏭️/❌ con el motivo.
- **Cancelar** detiene limpiamente; el avance queda guardado y se reanuda al volver a procesar.
- **Límite de la suscripción**: la cola se detiene y avisa «Se alcanzó el límite de tu plan. El avance quedó guardado;
  vuelve a pulsar Procesar más tarde.»
- **Menú contextual de cada fila** (clic derecho): procesar solo este, reintentar, abrir carpeta, abrir glosario,
  ver log (doble clic también) y abrir el log de traducción.
- **Selector rápido** de backend (Suscripción / API) y modelo (Opus / Sonnet / Haiku 5.5) en la barra superior.
- **Barra de estado**: video actual, progreso global y tokens con «incluido en la suscripción (equivalente API ~$X)»
  o el coste estimado con API. El panel «Registro» muestra los mensajes y errores (un error en un video nunca cierra la app).
- **Ajustes…**: backend, modelo, esfuerzo, tamaño de tramo (150), tiempo máximo por tramo, copia `.srt` sin idioma,
  carpeta de MKVToolNix (autodetectada; «Examinar…»), clave de API (se guarda cifrada con DPAPI; solo se muestran los
  últimos 4 caracteres) y «Comprobar», que hace lo mismo que `subtool doctor`. Se guardan en
  `%LOCALAPPDATA%\SubtitleTool\settings.json` (nunca con la clave).
- **Editor de glosario**: tablas editables de términos (inglés/español/nota) y tratamientos (A/B/tú-usted/nota);
  «Guardar» valida antes de escribir `<video>.glossary.json` y «Regenerar…» lo vuelve a generar con Claude.
- Para probar el flujo sin gastar límite ni dinero: abre la app con `SUBTOOL_FAKE_TRANSLATOR=1` (traductor falso).

## Comandos

| Comando | Qué hace |
|---|---|
| `subtool scan <ruta>` | Analiza un video (`.mkv`, `.mp4`, `.m4v`) o una carpeta (recursiva): pistas de subtítulos, pista elegida, estado del inglés y del español, y `.srt` sin código de idioma. |
| `subtool extract <ruta> [--out <carpeta>]` | Extrae la pista en inglés elegida (`mkvextract` para `.mkv`; `ffmpeg -map 0:N -c:s srt` para `.mp4`, que convierte `mov_text` a SRT). **Nunca sobrescribe**: si el archivo ya existe lo salta. |
| `subtool validate <original.srt> <traducido.srt> [--max-warnings N]` | Compara original y traducción (bloques, índices, tiempos, textos vacíos; avisos de etiquetas, líneas, longitud y guiones). |
| `subtool info <archivo.srt>` | Número de bloques, duración y primeros/últimos tiempos. |
| `subtool translate <archivo.en.srt> [opciones]` | Traduce al español con Claude → `.es.srt` (y copia `.srt` sin idioma si no existe). Reanudable. |
| `subtool glossary <archivo.en.srt>` | Genera (si no existe) y muestra el glosario editable `<video>.glossary.json`. |
| `subtool run <carpeta\|video> [opciones]` | Escanea → extrae el inglés → traduce lo que no tenga `.es.srt`. Resumen ✅/⏭️/❌ por video. |
| `subtool key set\|status\|clear` | Guarda la clave de API cifrada (solo para `--backend api`), muestra su origen (solo los últimos 4 caracteres) o la borra. |
| `subtool version` / `subtool update [--check]` | Muestra la versión / busca una nueva en GitHub y la instala (con `--check`, solo informa). |
| `subtool doctor` | Comprueba MKVToolNix, FFmpeg (informativo: solo para `.mp4`), Claude Code (versión y una llamada mínima con haiku) y si hay clave de API. ✅/❌ por punto. |

Códigos de salida: `0` correcto · `1` errores de validación/extracción · `2` uso incorrecto o herramienta no encontrada.

### Registro de errores

La ventana y la CLI escriben en `%LOCALAPPDATA%\SubtitleTool\logs\subtitulos-AAAA-MM-DD.log` (uno por día; se borran los
de más de 30 días): los mensajes del panel «Registro», los errores por video y la traza completa de cualquier error
inesperado (también si la app se cerrara). En la ventana: «Registro → Abrir carpeta de registros». Los logs detallados de
cada traducción siguen en la misma carpeta con el nombre del video.

### Elección de pista

Solo pistas de subtítulos en inglés (`eng`/`en`, o `language_ietf` que empiece por `en`) con codec de texto
(`S_TEXT/UTF8`, `S_TEXT/ASS`, `S_TEXT/SSA`, `S_TEXT/WEBVTT`). Preferencia: normal → SDH/CC → forzada (con aviso).
Empate: la marcada como `default`; si no, la de menor id. Si solo hay PGS/VobSub/DVB se informa que necesita OCR.
En `.mp4` se usa la misma lógica: `ffprobe` da el idioma, el título (`name`) y las marcas `default`/`forced`/`hearing_impaired`,
y los codecs se traducen a los de Matroska (`mov_text`/`subrip` → `S_TEXT/UTF8`, `dvd_subtitle` → `S_VOBSUB`…).
El id que se muestra es el índice de stream de ffmpeg.

### Nombres de salida

Junto al video (o en `--out`), con su mismo nombre base: `.en.srt` (`.en.sdh.srt` si es SDH), `.en.ass`, `.en.vtt`.
La traducción esperada es `.es.srt` / `.es.sdh.srt`.

## Traducción (Fase 2)

### Backends: `claude-code` (por defecto) o `api`

- **`--backend claude-code`** (predeterminado): cada llamada ejecuta `claude -p` (Claude Code instalado localmente) con tu
  sesión, así que **se usa tu suscripción de Claude** y no se paga por uso ni hace falta clave de API. El mensaje va por
  stdin y la respuesta se valida con `--json-schema` (mismo esquema que la API). Aislamiento: `--tools ""`,
  `--no-session-persistence`, `--strict-mcp-config`, `--disable-slash-commands`, `--setting-sources ""` (sin hooks,
  plugins ni ajustes de usuario/proyecto) y carpeta de trabajo temporal vacía (`%TEMP%\SubtitleTool-claude-cwd`), para que
  no cargue ningún `CLAUDE.md`. Se quitan `ANTHROPIC_API_KEY`/`ANTHROPIC_AUTH_TOKEN` del entorno de `claude` para que
  nunca facture por la API. (`--bare` no se usa: en ese modo Claude Code solo acepta clave de API e ignora la sesión.)
  - Tokens y "coste": se registran en el log (`total_cost_usd` de Claude Code) y en el progreso, pero se muestra como
    *incluido en la suscripción (equivalente API ~$X)*.
  - **Límite de uso de la suscripción**: si Claude Code responde que se alcanzó el límite, el trabajo se detiene **sin
    marcar bloques como fallidos** y con el progreso guardado; relanza el mismo comando cuando se restablezca
    (`run` deja de procesar más videos). Si no hay sesión iniciada, se indica que ejecutes `claude` → `/login`.
  - `--timeout MIN` (10 por defecto): tiempo máximo por llamada; si se supera, se mata el proceso y el trabajo se
    detiene (reanudable).
  - `--dry-run` no llama a nada: estima tokens localmente (caracteres/4) y muestra tramos y llamadas, sin dólares.
- **`--backend api`**: usa la API de Anthropic con tu clave (se paga por uso; `--dry-run` cuenta tokens con la API y estima el coste).

Cambiar de backend (o de modelo o tamaño de tramo) descarta el progreso guardado con un aviso.

### Clave de API de Anthropic (solo `--backend api`)

Orden de búsqueda:

1. Variable de entorno `ANTHROPIC_API_KEY` (tiene prioridad).
2. Archivo `%LOCALAPPDATA%\SubtitleTool\apikey.bin`, cifrado con DPAPI (solo lo puede leer tu usuario de Windows).

```powershell
subtool key set      # pide la clave sin mostrarla (o léela de stdin: Get-Content clave.txt | subtool key set)
subtool key status   # muestra el origen y los últimos 4 caracteres; nunca la clave completa
subtool key clear    # borra el archivo cifrado
```

La clave nunca se guarda en texto plano ni se escribe en los logs.

### Opciones de `translate` (también valen para `run`; `glossary` admite `--backend`, `--model` y `--effort`)

| Opción | Por defecto | Qué hace |
|---|---|---|
| `--backend claude-code\|api` | `claude-code` | Suscripción vía Claude Code o API de Anthropic con clave. |
| `--model claude-opus-5-5\|claude-sonnet-5-5\|claude-haiku-5-5` (o `opus\|sonnet\|haiku`) | `claude-opus-5-5` | Modelo. |
| `--effort low\|medium\|high` (con `claude-code` también `xhigh\|max`) | `medium` | Esfuerzo del modelo. |
| `--chunk N` | `150` | Bloques por tramo (los tramos van en orden, cada uno con contexto del anterior). |
| `--timeout MIN` | `10` | Solo `claude-code`: tiempo máximo por llamada. |
| `--dry-run` | | Solo estima (api: cuenta tokens y coste en USD; claude-code: estimación local, sin coste); no traduce. |
| `--restart` | | Descarta el progreso guardado y empieza de cero. |
| `--force` | | Sobrescribe el `.es.srt` si ya existe. |
| `--no-plain-copy` | | No crea la copia `Pelicula.srt` sin código de idioma. |
| `--regenerate-glossary` | | Vuelve a generar el glosario aunque exista. |

### Cómo funciona

- **Glosario**: primera pasada con todo el texto en inglés (sin tiempos) → `Pelicula.glossary.json` junto al `.en.srt`
  (personajes, lugares, términos y tú/usted entre personajes). Puedes editarlo; si existe, se reutiliza.
- **Tramos**: al modelo solo se envían `{id, text}` (nunca tiempos), con el glosario y los últimos 10 bloques ya
  traducidos como referencia. La respuesta es JSON estructurado; la app reconstruye cada bloque con su línea de tiempos original.
- **Validación y reintentos**: ids faltantes o vacíos se reintentan (solo esos, hasta 2 veces); si se corta por
  `max_tokens`, el tramo se divide en dos; si el modelo se niega, se reintenta en mitades una vez y, si persiste,
  esos bloques quedan como fallidos (con la categoría del rechazo en el log) y el resto continúa.
- **Resultado**: solo se escribe `Pelicula.es.srt` (o `.es.sdh.srt`) si todos los bloques están traducidos y
  `validate` da 0 errores (UTF-8, CRLF). Además se crea `Pelicula.srt` (para que el reproductor lo cargue solo)
  **solo si no existe ya** un `.srt` sin idioma; si existe, no se toca y se avisa.
- **Reanudación**: el progreso se guarda en `%LOCALAPPDATA%\SubtitleTool\jobs\<hash>\` tras cada tramo. Si se corta
  (Ctrl+C, error de red, bloques fallidos), relanza el mismo comando: se saltan los tramos ya hechos. Se borra al terminar bien.
- **Logs**: `%LOCALAPPDATA%\SubtitleTool\logs\` — un archivo por trabajo con cada llamada (tramo, ids, tokens,
  `stop_reason`, duración, errores).
- **Precios usados en la estimación** (USD por millón de tokens, entrada/salida): Opus $4/$20, Sonnet $2/$10,
  Haiku $0,10/$0,50; lectura de caché al 5 % de la entrada y escritura al 125 %. Salida estimada ≈ 1,3 × tokens del inglés.

### Modo de prueba sin API

Con la variable de entorno `SUBTOOL_FAKE_TRANSLATOR=1` se usa un traductor falso determinista (antepone `ES: `
a cada bloque) que no llama a la API ni necesita clave. Útil para probar el flujo completo.

## Ejemplos

```powershell
subtool scan "D:\Peliculas"
subtool extract "D:\Peliculas\Pelicula.mp4"
subtool extract "D:\Peliculas" --out "D:\Subs"
subtool validate "Pelicula.en.srt" "Pelicula.es.srt" --max-warnings 50
subtool info "Pelicula.en.srt"
subtool doctor
subtool translate "Pelicula.en.srt" --dry-run
subtool translate "Pelicula.en.srt"                       # suscripción vía Claude Code, Opus
subtool translate "Pelicula.en.srt" --model sonnet --effort high
subtool translate "Pelicula.en.srt" --backend api --model claude-sonnet-5-5
subtool run "D:\Peliculas" --dry-run
subtool run "D:\Peliculas"
```

## Estructura

- `src/SubtitleTool.Core` — librería: localización de MKVToolNix, `Media/` (FFmpeg para `.mp4`: localizador, `ffprobe`,
  extracción, y `MediaToolkit`, que elige herramienta según la extensión), `Diagnostics/AppLog` (registro diario), ejecución de procesos, inspección (`mkvmerge -J`),
  selección de pista, nombres de salida, extracción, escaneo de biblioteca, SRT (parser, writer, validador) y
  traducción (`Translation/`: clientes de Claude —API y Claude Code—, traductor por tramos, glosario, estado de reanudación, clave, precios).
  `Workflow/` reúne lo que comparten la CLI y la interfaz: decisión por video (`VideoWorkflow.Plan`), formato de coste y
  detalle, creación del cliente de traducción (`TranslationClientFactory`, incluido el traductor falso), comprobaciones de
  `doctor` (`DoctorService`) y el procesamiento completo de un video (`VideoProcessor`: extraer si falta → traducir).
- `src/SubtitleTool.Cli` — aplicación de consola `subtool`.
- `installer/` — `Subtitulos.iss` (Inno Setup) y `build-installer.ps1`.
- `src/SubtitleTool.App` — interfaz gráfica WPF `Subtitulos.exe` (MVVM con CommunityToolkit.Mvvm): `ViewModels/`,
  `Views/`, `Services/` (adaptadores a Core y diálogos) y `Themes/Styles.xaml`.
- `tests/SubtitleTool.App.Tests` — pruebas de las ViewModels con servicios falsos (sin abrir ventanas ni llamar a Claude).
- `tests/SubtitleTool.Core.Tests` — pruebas xUnit (con fixtures JSON de `mkvmerge -J` y de `ffprobe` de un `.mp4` real, un cliente de traducción falso y salidas grabadas de
  `claude -p`; sin red ni Claude Code real).
