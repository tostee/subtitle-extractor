# Task 2 — Versión cubana (vulgar) de los subtítulos

- **Fecha:** 2026-11-10
- **Estado:** Completada (publicada en 1.3.0)

## De qué trata
Agregar una opción para que los subtítulos se traduzcan con habla cubana, lo más vulgar posible, para uso personal
(ver películas en grupo con los compañeros y reírse).

## Impacto
- Nueva acción en la app: clic derecho en una fila → «Versión cubana (vulgar, para reírse)…». En consola: `subtool translate <x.en.srt> --cubano`.
- Genera un archivo aparte, `<video>.es-cu.srt` (`.es-cu.sdh.srt` si es SDH). **No afecta a la traducción normal**: no toca
  el `.es.srt`, no crea la copia `.srt` sin idioma, no usa la revisión, no cambia la columna «Español» ni el estado de la fila
  como «ya traducido», y guarda su avance de reanudación por separado.
- Gasta límite de la suscripción (o dinero con la API) igual que una traducción normal.

## Resumen de lo que se hizo
- **Core**
  - `TranslationModels.cs`: enum `TranslationStyle` (`Neutral`, `Cuban`) y `TranslationOptions.Style`.
  - `TranslationPaths.cs`: `CubanPath` y `OutputPath(style)`.
  - `TranslationPrompts.cs`: `CubanSystemPrompt` (cubano de barrio, todos tutean, groserías y doble sentido cubanos, sin
    regionalismos de otros países) con las mismas reglas técnicas que el normal (etiquetas, guiones, 2 líneas, ids). `SystemPromptFor`/`PromptVersionFor`.
  - `SubtitleTranslator.cs`: con estilo cubano escribe en `CubanPath`, usa el prompt cubano, fuerza sin revisión y sin copia
    sin idioma, e ignora los borradores de revisión de la traducción normal.
  - `JobStore.cs`: el avance cubano se guarda en `<hash>-cu` (no pisa el de la traducción normal).
  - `VideoProcessor.cs`: con estilo cubano no se salta el video por tener `.es.srt` o revisión pendiente; extrae el inglés si
    falta; el detalle empieza por «versión cubana:» y el registro se llama `*.cubano.log`. `VideoOutcome.Style`.
- **App**: `AppSettings.Style/Force` (no se guardan), `VideoItemViewModel` (`CanTranslateCuban`, `CubanPath`, estilo en cola,
  resultado cubano sin tocar la columna Español), `MainViewModel.TranslateCubanCommand` (pregunta antes de rehacer si ya
  existe) y la entrada del menú contextual en `MainWindow.xaml`.
- **CLI**: opción `--cubano` en `translate` y en la ayuda.
- **README**: sección «Versión cubana (para reírse)» y fila en la tabla de opciones.
- **Verificación**
  - Pruebas nuevas: `tests/SubtitleTool.Core.Tests/CubanStyleTests.cs` (6) y `tests/SubtitleTool.App.Tests/CubanVersionTests.cs` (2).
    Suite completa: 177 + 92 pruebas pasan; compila sin advertencias.
  - Prueba real con Claude Code (Sonnet, esfuerzo bajo) sobre un SRT de 6 bloques: 0 errores de validación; salida en cubano
    de barrio («¿Qué vuelta es esa, singao?», «Vamos a jamar, mi'jo»).
  - No se abrió la ventana WPF a mano: el menú se verificó con pruebas del ViewModel.
