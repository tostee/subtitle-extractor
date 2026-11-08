# Cambios

Formato: una sección `## [X.Y.Z] - AAAA-MM-DD` por versión. Al publicar la etiqueta `vX.Y.Z`, la sección correspondiente
se usa como notas de la Release (y es lo que la app muestra en «Novedades»).

## [1.0.2] - 2026-11-08

- La aplicación se instala ahora en `C:\Program Files\Subtitulos` para todos los usuarios (pide permiso de
  administrador al instalar y al actualizar).
- Si estaba instalada la 1.0.0 o la 1.0.1 (en el perfil del usuario), esa copia se quita automáticamente; los ajustes,
  la clave, los glosarios y el avance se conservan.
- Tras actualizar, la app se vuelve a abrir con el usuario normal (no como administrador).

## [1.0.1] - 2026-11-08

- Icono propio de la aplicación: en el instalador, el menú Inicio, el escritorio, la barra de tareas, las ventanas y en
  «Aplicaciones instaladas» de Windows.

## [1.0.0] - 2026-11-08

Primera versión publicada.

- Extrae los subtítulos en inglés de videos `.mkv` (MKVToolNix) y `.mp4`/`.m4v` (FFmpeg), eligiendo la pista normal antes
  que la SDH o la forzada. Nunca sobrescribe archivos existentes.
- Traduce al español latinoamericano neutro con Claude: con tu suscripción a través de Claude Code (por defecto) o con la
  API de Anthropic. Por tramos, con glosario editable, reanudable y con validación del resultado.
- Ventana `Subtítulos`: lista de videos con arrastrar y soltar, cola secuencial, progreso, resumen, editor de glosario y
  ajustes. Consola `subtool` con los mismos comandos.
- Registro diario de actividad y errores en `%LOCALAPPDATA%\SubtitleTool\logs`.
- Instalador sin permisos de administrador y sin necesidad de .NET; ofrece instalar MKVToolNix y FFmpeg con winget.
- Actualización automática desde GitHub: aviso al abrir la app, descarga verificada con SHA-256 e instalación con un clic.
