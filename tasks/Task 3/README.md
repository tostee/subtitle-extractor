# Task 3 — Publicar la versión 1.3.0

- **Fecha:** 2026-11-10
- **Estado:** Completada

## De qué trata
Publicar como 1.3.0 la versión cubana de los subtítulos ([Task 2](../Task%202/README.md)) y hacer el commit.

## Impacto
- Las apps instaladas verán la actualización 1.3.0 al abrirse (Release de GitHub con el instalador y su `.sha256`).
- Las notas de la Release salen de la sección `[1.3.0]` de `CHANGELOG.md`.

## Resumen de lo que se hizo
- `Directory.Build.props`: `<Version>` 1.2.0 → 1.3.0.
- `CHANGELOG.md`: sección `## [1.3.0] - 2026-11-10`.
- Pruebas locales pasadas antes del commit.
- Commit en `main` con la funcionalidad, los registros `tasks/`/`bugs/` y `CLAUDE.md` (la carpeta `docs/`, que ya estaba sin seguimiento, se dejó fuera).
- Etiqueta `v1.3.0` y `git push origin main v1.3.0`: el workflow `release.yml` compila el instalador y crea la Release.
