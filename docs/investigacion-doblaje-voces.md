# Investigación: doblaje con voces a partir de los subtítulos cubanos

Fecha: 2026-11-13 · Relacionado: [Task 4](../tasks/Task%204/README.md)

## Conclusión corta

**Sí es posible y gratis.** Ya tenemos lo más difícil (el `.srt` en cubano con tiempos).
Lo que falta es: *texto → voz* por cada línea, ajustar la duración al hueco del subtítulo,
y mezclar con la pista original (bajándole el volumen a las voces originales o quitándolas).

## Voces con acento cubano (lo más importante para la gracia)

| Opción | Acento cubano | Coste | Notas |
|---|---|---|---|
| **edge-tts** (voces neurales de Microsoft Edge) | ✅ `es-CU-ManuelNeural` (hombre), `es-CU-BelkysNeural` (mujer) | Gratis, sin clave | **Verificado hoy**: genera MP3 al instante. Requiere internet. Permite `--rate`, `--pitch`, `--volume`. Sólo 2 voces cubanas. |
| Clonación de voz local (F5-TTS, CosyVoice, IndexTTS-2, Chatterbox, XTTS v2, GPT-SoVITS) | ⚠️ Sólo si se le da una muestra de un cubano real (5–15 s) | Gratis, local | Necesitan GPU NVIDIA para ir rápido. Permiten tener muchos "personajes". Calidad en español variable; hay que probar. |
| Piper (local, ligero) | ❌ No hay voz cubana | Gratis | Sirve como respaldo sin internet, acento neutro/peninsular. |
| ElevenLabs / Azure de pago | ✅ | De pago | Fuera del objetivo "free". |

**Recomendación:** empezar con **edge-tts** (cero instalación pesada, acento cubano real) y
dejar la clonación de voz como fase 2 si se quieren más personajes.

## Repos gratuitos que ya hacen el pipeline completo

| Repo | Qué hace | Encaje con nosotros |
|---|---|---|
| **pyVideoTrans** (`jianchang512/pyvideotrans`, GPL-3.0) | Transcribe, traduce, dobla y mezcla. Tiene `.exe` para Windows. Tarea CLI `--task tts` que **dobla directamente desde un `.srt`**. Soporta Edge-TTS, F5-TTS, CosyVoice, GPT-SoVITS, Index-TTS, ChatterBox… Multi-voz por hablante. | **El mejor para probar ya** sin programar: le pasamos nuestro `.srt` cubano y la voz `es-CU-ManuelNeural`. |
| **VideoLingo** | Subtítulos + doblaje "todo en uno". | Más orientado a generar/traducir subtítulos; redundante con nuestra app. |
| **Voice-Pro**, **OmniVoice Studio** | Apps locales de doblaje + clonación. | Alternativas para comparar. |
| **Linly-Dubbing**, **Python-Autodub** | Pipelines con Demucs (separar voz/música) + pyannote (quién habla) + F5-TTS. | Referencia técnica si lo integramos nosotros. |

## Cómo sería integrarlo en nuestra app (fase futura)

1. **Por cada línea del `.srt` cubano** → llamar a edge-tts (o a un motor local) → un clip de audio.
2. **Ajuste de tiempo**: si el clip dura más que el hueco del subtítulo, acelerarlo con
   `ffmpeg -af atempo=X` (hasta ~1.5× suena natural; más allá, recortar texto o invadir el hueco siguiente).
   En edge-tts también se puede pedir `--rate=+20%` de entrada.
3. **Pista de fondo**:
   - Simple: bajar el volumen del audio original ("ducking") mientras habla la voz nueva.
   - Mejor: separar voces y música con **Demucs** (gratis, local) y dejar sólo música/efectos + voz cubana.
4. **Mezclar y multiplexar** con ffmpeg / MKVToolNix (que la app ya usa) como **pista de audio
   adicional** "Español (cubano, doblaje IA)", sin tocar la original.
5. **Personajes** (fase 2): diarización (pyannote/WhisperX) para saber quién habla y asignar
   voz de hombre/mujer o voces clonadas distintas.

Para .NET hay dos caminos: invocar `edge-tts` (Python) como proceso externo, igual que hacemos
con otras herramientas, o usar una librería C# que hable el mismo protocolo WebSocket.

## Riesgos / cosas a tener en cuenta

- **edge-tts** usa un servicio no oficial de Microsoft: puede cambiar o cortarse sin aviso; uso personal OK.
- Las voces sintéticas leen "bien" pero sin actuación: el humor depende mucho del texto
  (ya lo tenemos) y de ajustar `rate`/`pitch` por personaje.
- Clonar la voz de una persona real requiere su permiso.
- pyVideoTrans es GPL-3.0: usarlo como herramienta externa no afecta, copiar su código sí.

## Prueba realizada

```
pip install edge-tts
edge-tts --voice es-CU-ManuelNeural --rate=+10% \
  --text "Asere, ¿qué bolá? Esto está en talla, compadre. ¡Dale, que se nos va la guagua!" \
  --write-media prueba_cubano.mp3
```
Resultado: MP3 de 47 KB generado correctamente.

## Fuentes

- https://github.com/jianchang512/pyvideotrans
- https://www.opensourcealternatives.to/item/pyvideotrans
- https://blog.brightcoding.dev/2026/07/09/videolingo-the-ai-subtitle-revolution-every-creator-needs
- https://weihaoqu.github.io/learnAIDoc/wiki/voice-pro-ai-dubbing-translation/
- https://themenonlab.blog/blog/omnivoice-studio-open-source-local-dubbing-646-languages
- https://hn.jakechampion.name/reader/https://github.com/Kedreamix/Linly-Dubbing
- https://www.siliconflow.com/articles/en/best-open-source-models-for-voice-cloning
- https://findskill.ai/blog/best-open-source-tts-2026/
- https://speechgen.io/en/tts-spanish-cuba/
- https://tts.ai/voices/piper/?lang=es

## Prueba de punta a punta (hecha)

Ver [Task 4](../tasks/Task%204/README.md). Fragmento de 2 minutos de *The Odyssey* doblado al cubano con
edge-tts + ffmpeg en ~1 minuto de proceso, sin GPU. Truco clave: en audio 5.1 los diálogos van en el
**canal central**; quitándolo se obtiene música y efectos sin voces en inglés y sin necesidad de Demucs.
Script y resultado en `D:\Multimedia\Subtittle extractor\_prueba_doblaje\`.

## Mejorar las voces (repos integrables)

Problema: edge-tts solo tiene 2 voces cubanas y diferenciar personajes bajando el tono suena artificial.
Hardware disponible: RTX 5060 Laptop (8 GB), suficiente para todo lo de abajo.

| Opción | Qué aporta | Acento cubano | Licencia |
|---|---|---|---|
| **edge-tts + Seed-VC** (`Plachtaa/seed-vc`) | Conversión de voz *zero-shot*: toma el audio de Manuel (acento y ritmo cubanos) y le cambia el timbre por el de una muestra de 1–30 s, sin entrenar. Un timbre por personaje. | ✅ se conserva (viene de edge-tts) | GPL-3.0 |
| **Chatterbox Multilingual** (`resemble-ai/chatterbox`) | TTS con clonación; tiene control de **exageración/emoción**, ideal para comedia. Checkpoints de español latino y de España. | Solo si la muestra de referencia es de un cubano | MIT (añade marca de agua inaudible) |
| **F5-TTS + F5-Spanish** (`jpgallegoar/F5-Spanish`) | Clonación muy fiel con 5–15 s de muestra; afinado en español. | Solo con muestra cubana | Pesos CC-BY-NC (uso personal OK) |
| RVC / Applio | Igual que Seed-VC pero hay que entrenar un modelo por voz (más calidad, más trabajo). | ✅ se conserva | MIT |

**Recomendación:** edge-tts + **Seed-VC**. Mantiene lo que ya funciona (acento cubano real) y da voces
distintas por personaje con muestras de unos segundos (p. ej. grabaciones de amigos, con su permiso).
Como alternativa, Chatterbox clonando una muestra cubana, con más expresividad.

Fuentes: https://github.com/resemble-ai/chatterbox · https://huggingface.co/jpgallegoar/F5-Spanish ·
https://pypi.org/project/seed-vc/ · https://arxiv.org/html/2411.09943v1

## Prueba con Seed-VC (hecha)
edge-tts (acento cubano) + Seed-VC (timbre por personaje) funciona en la RTX 5060: 40 líneas en ~100 s,
sin cambiar la duración de los clips, así que la sincronización no se ve afectada. Detalle en Task 4.

## Emociones (ira, sorpresa, alegría…)

- **edge-tts cubano:** las voces `es-CU` no tienen estilos (`angry`, `cheerful`…; sus metadatos solo dicen "Friendly, Positive") y edge-tts ya no acepta SSML propio. Solo se puede imitar la emoción con velocidad, tono, volumen y puntuación por línea.
- **IndexTTS-2.5** (`index-tts/index-tts`): español incluido; emoción con un vector de 8 valores (alegre, ira, triste, miedo, asco, melancolía, sorpresa, calma), con un audio de referencia emocional o con texto. Clona la voz a partir de una muestra. Licencia de modelo de bilibili. Sin control exacto de duración (solo `duration_factor` 0,5–2×).
- **Qwen3-TTS** (`QwenLM/Qwen3-TTS`, Apache-2.0): español incluido; emoción por instrucción en lenguaje natural ("muy enfadado", "incrédulo con pánico"). En el modelo de clonación no hay instrucciones; se diseña la voz con VoiceDesign y luego se clona.
- **Idea de flujo:** que Claude etiquete la emoción de cada línea al hacer la versión cubana; IndexTTS-2.5 genera con la emoción usando como muestra la voz cubana de Manuel (para intentar conservar el acento) y Seed-VC pone el timbre del personaje. Falta probar si el acento cubano sobrevive.

Fuentes: https://github.com/index-tts/index-tts · https://github.com/QwenLM/Qwen3-TTS · https://github.com/diodiogod/TTS-Audio-Suite

## Prueba con IndexTTS-2.5 (hecha)
Las emociones se notan y se miden (tono, energía, duración). Problemas: alarga frases (hace falta
`duration_factor`), ~3/40 palabras mal pronunciadas y Seed-VC aplana parte de la emoción (sobre todo la ira).
Detalle completo en Task 4.
