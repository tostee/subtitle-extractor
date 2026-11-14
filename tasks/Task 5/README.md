# Task 5 — Integrar el doblaje con voces en la aplicación (vista nueva)

- **Fecha:** 2026-11-14
- **Estado:** Completada (sin publicar; pendiente de commit)
- **Commit(s):** —

## De qué trata
"Queda mucho por mejorar, de momento vamos a integrar esta nueva funcionalidad a la aplicación, puede ser con una vista nueva solo para eso, aparte de la que ya tenemos para procesar los subtítulos."

Llevar a la app lo probado en la [Task 4](../Task%204/README.md): a partir del `.es-cu.srt` generar voces (edge-tts / IndexTTS-2.5 con emociones / Seed-VC con voces propias) y añadir una pista de audio de doblaje al video.

## Impacto
- Ventana nueva **«Doblaje con voces»** (botón «Doblaje…» de la barra y clic derecho → «Doblar con voces…»), aparte de
  la ventana de subtítulos. No cambia nada del flujo de extracción/traducción.
- Crea `<video>.doblaje-cu.mkv` (o `….prueba-HHMMSS-HHMMSS.mkv` para un fragmento) junto al video, y
  `<video>.es-cu.doblaje.json` junto al SRT. Los videos generados no entran en la lista al escanear carpetas.
- Los motores de voz se instalan desde la ventana en `%LOCALAPPDATA%\SubtitleTool\voces` o en la carpeta elegida
  (nuevo ajuste `dubbingEnginesDirectory`). Los audios intermedios van a `%LOCALAPPDATA%\SubtitleTool\doblaje`.
- El instalador de la app incluye los scripts Python en `Dubbing\` (sin cambios en `Subtitulos.iss`).

## Resumen de lo que se hizo
Decisiones del usuario: Claude detecta personajes y emociones; la app instala los motores; entran los tres motores.

**Motor (Python, `src/SubtitleTool.App/Dubbing/`)**
- `motor_doblaje.py`: generaliza el prototipo de la Task 4. Lee un plan JSON, genera las voces (edge-tts en paralelo o
  IndexTTS por lotes), aplica Seed-VC, ajusta tiempos (atempo ≤ 1,6×, nunca se pisan), escribe la pista de voces de forma
  secuencial (sirve para 1.500 líneas) y mezcla con ffmpeg: fondo sin canal central en 5.1/7.1 (canales por posición) o
  con *ducking* en estéreo. Película entera: video copiado, todas las pistas y adjuntos originales. Progreso como líneas
  JSON. Caché por contenido: si se cancela, se retoma sin regenerar.
- `lote_seedvc.py`, `lote_indextts.py`: cargan el modelo una vez y procesan lotes.
- Hallazgo: con la muestra de acento a 48 kHz IndexTTS tardaba ~230 s en la primera línea; a 24 kHz, 24 s.

**Núcleo (`src/SubtitleTool.Core/Dubbing/`)**
- `DubbingProject` (+ `DubbingProjectStore`): líneas, personajes, voces; reaplica etiquetas solo a líneas que siguen igual.
- `DubbingAnnotator`: etiqueta por tramos con el mismo cliente de la traducción (`personaje | sexo | emoción | intensidad`),
  reintenta ids omitidos, pasa los personajes ya conocidos y el glosario.
- `DubbingEngines`: instala con uv 0.11.8 (SHA-256 verificado) todo dentro de la carpeta elegida; versiones fijadas
  (edge-tts 7.3.1, Seed-VC 51383ef, IndexTTS d9e41aa, modelo IndexTTS-2.5 c39ce5b, PyTorch 2.7.1/2.8 cu128); prueba final.
- `DubbingRunner`: construye el plan y sigue el progreso. `ProcessRunner`: lectura línea a línea, variables de entorno y
  cierre de los procesos hijos al salir de la app.

**App**: `DubbingViewModel`, `DubbingWindow` (pestañas Personajes, Líneas, Motores, Registro; opciones de voz, voces propias,
fragmento y fuerza de las emociones), `DubbingService`, diálogos nuevos, comando en `MainViewModel`.

**Verificación**
- Pruebas: 220 de Core y 111 de la app (19 nuevas de la ventana, 40 de doblaje en Core). Estables en 5 repeticiones.
- Prueba real con el código C#: instalación del motor base desde cero (20 s), etiquetado de 40 líneas con Claude (20 s;
  confundió a Agamenón con «Sombra», corregible a mano) y doblaje de un fragmento (28 s).
- Motor con IndexTTS + Seed-VC + voces del usuario: 40 líneas en 8 min.
- Ventana renderizada a PNG sin errores de enlace.
- Revisión de código independiente: 1 fallo alto (líneas iguales generadas a la vez sobre el mismo archivo) y 7 medios
  corregidos (grafía del personaje, edición concurrente, etiquetas desplazadas, procesos huérfanos, videos generados en
  la biblioteca y temporales, caché de la regeneración rápida, 7.1(wide)/5.1 sin layout), más varios menores.

**Pendiente / no verificado**
- La instalación de Seed-VC e IndexTTS **desde la app** no se ha probado de punta a punta (≈ 20 GB de descarga); sí sus
  pasos por separado durante la Task 4.
- No probado con audio estéreo ni con un `.mp4`.

**Limpieza (a petición del usuario)**
- Borradas las pruebas temporales en C: (~8,4 GB). La caché global de uv (33,5 GB) se dejó, por decisión del usuario.
- Carpeta de motores configurada en `D:\Multimedia\Subtittle extractor\motores-voz` (ajuste `dubbingEnginesDirectory`).
  La instalación de prueba manual de la Task 4 (`D:\Multimedia\Subtittle extractor\_herramientas_voz`, ~12 GB) ya no la usa la app.
