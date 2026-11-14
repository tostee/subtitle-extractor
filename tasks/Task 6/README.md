# Task 6 — Publicar la versión 1.4.0 (doblaje con voces)

- **Fecha:** 2026-11-14
- **Estado:** Commit hecho; falta la etiqueta v1.4.0 y el push (pendiente de confirmación)
- **Commit(s):** «Subtítulos 1.4.0: doblaje con voces cubanas (ventana aparte)»

## De qué trata
"Sí, haz el commit y prepara la 1.4.0" — publicar como 1.4.0 el doblaje con voces ([Task 5](../Task%205/README.md)).

## Impacto
- Al subir la etiqueta `v1.4.0`, el workflow `release.yml` compila el instalador y crea la Release; las apps instaladas
  verán la actualización al abrirse. Las notas salen de la sección `[1.4.0]` de `CHANGELOG.md`.

## Resumen de lo que se hizo
- `Directory.Build.props`: `<Version>` 1.3.0 → 1.4.0. `CHANGELOG.md`: «Sin publicar» → `## [1.4.0] - 2026-11-14`.
- Pruebas en Release: 220 (Core) + 111 (App), todas pasan.
- `dotnet publish` win-x64 como en `build-installer.ps1`: la carpeta incluye `Dubbing\motor_doblaje.py`,
  `lote_seedvc.py` y `lote_indextts.py`; `Subtitulos.exe` con versión 1.4.0. (Inno Setup no está instalado en local:
  el instalador lo compila el workflow.)
- Commit en `main` con las Tasks 4–6, la funcionalidad y `docs/investigacion-doblaje-voces.md` (el otro documento de
  `docs/`, ya sin seguimiento, se deja fuera).
