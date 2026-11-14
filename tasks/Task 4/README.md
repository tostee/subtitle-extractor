# Task 4 — Investigar doblaje con voces (cubano) a partir de los subtítulos

- **Fecha:** 2026-11-13
- **Estado:** Completada (investigación + prueba)
- **Commit(s):** —

## De qué trata
"Necesito hacer una investigación para ver si es posible o si hay repos free que cojan un video y generen audios, o textos audios, la idea es tener una vía para hacer de cierta manera algo cómico, ya tenemos los subtítulos en cubano, pero ponerle voces sería más enganchador."

## Impacto
Sólo investigación, sin cambios en la app. Deja la base para una posible función futura de "doblaje IA" en cubano como pista de audio adicional.

## Resumen de lo que se hizo
- Investigados repos gratuitos de doblaje (pyVideoTrans, VideoLingo, Voice-Pro, OmniVoice Studio, Linly-Dubbing) y motores TTS (edge-tts, F5-TTS, CosyVoice, IndexTTS-2, Chatterbox, XTTS v2, Piper).
- Verificado que edge-tts tiene voces cubanas reales (`es-CU-ManuelNeural`, `es-CU-BelkysNeural`) y generado un MP3 de prueba.
- Documento completo con recomendación y plan de integración: [`docs/investigacion-doblaje-voces.md`](../../docs/investigacion-doblaje-voces.md).

### Prueba de punta a punta (2026-11-13)
- Fragmento de *The Odyssey (2026)*, 01:24:30–01:26:34 (escena del inframundo, 40 líneas, 3 personajes). No existía un `.es-cu.srt` de la película, así que el texto cubano de esas 40 líneas se escribió a mano siguiendo el estilo de `CubanSystemPrompt`.
- Script `dub.py` (Python + edge-tts + ffmpeg), copiado en `D:\Multimedia\Subtittle extractor\_prueba_doblaje\`:
  1. Una voz por línea con `es-CU-ManuelNeural`; los 3 personajes se distinguen por el tono (0 / −12 / −25 Hz).
  2. Se recorta el silencio de cada clip, se acelera hasta 1,6× para que quepa antes del siguiente subtítulo y, si aun así no cabe, se retrasa la siguiente línea (nunca se pisan).
  3. Fondo: mezcla del 5.1 **sin el canal central** (ahí van los diálogos en inglés; en el fragmento el central suena ~8 dB más que el frontal izquierdo), con *ducking* cuando habla la voz cubana.
  4. Salida `Odisea_prueba_doblaje_cubano.mkv` (720p, 2:04): pista de audio "Español (cubano, doblaje IA)" por defecto, pista original en inglés y subtítulos cubanos.
- Resultado: 40 líneas, 14 aceleradas, solo 2 desbordan (0,01 s y 0,2 s), retraso máximo 0,2 s. Detalle en `ajuste_tiempos.json`.

### Ajuste tras probarlo el usuario (2026-11-13)
- "No lee bien las palabras con ', ejemplo pa', porque hace una pausa": el texto que va a la voz ahora quita los apóstrofos (`pa'` → `pa`, `pa'l` → `pal`, `mi'jo` → `mijo`, `to'` → `to`); el `.srt` los conserva. Medido con `silencedetect`: las pausas internas bajan de 2→1, 2→1 y 3→2 en las líneas afectadas (las que quedan son las comas).
- Pregunta sobre mejores voces: ver sección "Mejorar las voces" en `docs/investigacion-doblaje-voces.md`.

### Prueba con Seed-VC y voces de ejemplo (2026-11-13)
- Instalado Seed-VC (`Plachtaa/seed-vc`, commit 51383ef) en un entorno aislado con Python 3.10 y PyTorch 2.7.1+cu128 (necesario para la RTX 5060). Descarga: ~3,5 GB de PyTorch y dependencias + ~1–1,5 GB de modelos la primera vez.
- `batch_vc.py`: carga el modelo una sola vez y convierte una lista de clips (el `inference.py` del repo recarga el modelo en cada llamada).
- `dub.py` admite `VC_REFS`: genera la voz cubana sin cambiar el tono y Seed-VC le pone el timbre de cada personaje. Voces de ejemplo anónimas del repo (se descartaron las de personas o personajes reales): Odiseo `reference/s4p2.wav`, fantasma `source/source_s3.wav`, Agamenón `source/source_s2.wav`.
- Resultado: `Odisea_prueba_doblaje_cubano_seedvc.mkv`. 40 clips convertidos en 101 s; la duración de cada clip no cambia. Tono medio: Odiseo 121→179 Hz, fantasma 112→108 Hz, Agamenón 120→96 Hz. Desbordan 4 líneas (máx. 0,21 s).
- Pregunta sobre emociones (ira, sorpresa, alegría): investigado, ver sección "Emociones" en `docs/investigacion-doblaje-voces.md`.

### Prueba con voces propias del usuario (2026-11-13)
- El usuario grabó 3 notas de voz (`_prueba_doblaje/voces/odiseo|fantasma|agamenon.ogg`, 23–37 s). Preparadas en `voces/prep/` (paso alto 70 Hz, quitar silencios > 0,4 s, `loudnorm`, máx. 24 s).
- `dub.py` acepta `VC_SUFFIX` para el nombre de salida. Resultado: `Odisea_prueba_doblaje_cubano_voces_propias.mkv` (40 clips en 123 s).
- El tono del doblaje se acerca al de cada muestra (Odiseo 246→224 Hz, fantasma 171→149 Hz, Agamenón 132→139 Hz; la voz base de Manuel está en ~120 Hz).

### Prueba con IndexTTS-2.5 y emociones (2026-11-13)
- Instalado en `D:\Multimedia\Subtittle extractor\_herramientas_voz\index-tts` (commit d9e41aa; `uv sync`, PyTorch 2.8+cu128; entorno 8,1 GB + modelo 4 GB, sin el módulo Qwen de emoción por texto). Lote: `_herramientas_voz\idx_batch.py`.
- Voz de referencia: 17 s de `es-CU-ManuelNeural` (`_prueba_doblaje/voces/acento/manuel.wav`) para intentar conservar el acento. Emoción por línea con el vector de 8 valores (`EMOS` en `dub.py`, asignadas a mano).
- Prueba de una frase con 5 emociones: ira/sorpresa/alegría suben el tono (107 → 230–260 Hz) y la energía (+8–11 dB); la tristeza alarga mucho (5,7 → 11,8 s).
- `dub.py`: `ENGINE=indextts`; segunda pasada con `duration_factor` para las líneas que no caben ni acelerando 1,5× (14 de 40); `IDX_REUSE=1` reutiliza los audios; el silencio se recorta **antes** de Seed-VC (si no, Seed-VC lo convierte en ruido y desincroniza: llegó a haber 4 s de retraso).
- Resultados: `Odisea_prueba_doblaje_cubano_emociones.mkv` (voz cubana clonada) y `..._emociones_voces_propias.mkv` (+ Seed-VC con las voces del usuario). 3 líneas desbordan (máx. 0,7 s). 40 líneas: ~7 min de IndexTTS + ~2 min de Seed-VC.
- Verificación con Whisper-small: 3 de 40 líneas mal pronunciadas ("¿Qué muertos?" → "¡Pe! Muertos", "dejaste tirados" → "dejas tetigados", "devuélvele" → "te vuelve"); el resto de avisos eran nombres/cubanismos.
- Seed-VC aplana parte de la emoción: en una línea de ira la energía baja 6,5 dB y la variación del tono 20 % → 12 %; la sorpresa se conserva mejor.
- Por error, la ejecución sin Seed-VC iba a sobrescribir el `.mkv` de edge-tts (mismo nombre); se copió antes como `Odisea_prueba_doblaje_cubano_edge.mkv`.
