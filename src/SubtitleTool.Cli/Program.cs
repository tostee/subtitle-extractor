using System.Text;
using SubtitleTool.Cli;
using SubtitleTool.Core.Diagnostics;
using SubtitleTool.Core.Library;
using SubtitleTool.Core.Media;
using SubtitleTool.Core.Mkv;
using SubtitleTool.Core.Srt;
using SubtitleTool.Core.Translation;

Console.OutputEncoding = new UTF8Encoding(false);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
    Console.Error.WriteLine("Cancelando…");
};

try
{
    return await Commands.RunAsync(args, cts.Token);
}
catch (UsageException ex)
{
    Console.Error.WriteLine($"Error de uso: {ex.Message}");
    Console.Error.WriteLine("Ejecuta 'subtool help' para ver los comandos.");
    return ExitCodes.Usage;
}
catch (Exception ex) when (ex is MkvToolNixNotFoundException or FfmpegNotFoundException)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return ExitCodes.Usage;
}
catch (FileNotFoundException ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return ExitCodes.Usage;
}
catch (TranslationClientException ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return ExitCodes.Failure;
}
catch (InvalidDataException ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return ExitCodes.Failure;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Operación cancelada.");
    return ExitCodes.Failure;
}
catch (Exception ex)
{
    // Error inesperado: se guarda la traza completa en el registro para poder diagnosticarlo.
    AppLog.Default.WriteException("cli", "subtool " + string.Join(' ', args), ex);
    Console.Error.WriteLine($"Error inesperado: {ex.Message}");
    Console.Error.WriteLine($"Detalles en: {AppLog.Default.CurrentFile}");
    return ExitCodes.Failure;
}

namespace SubtitleTool.Cli
{
    internal static class ExitCodes
    {
        public const int Ok = 0;
        public const int Failure = 1;
        public const int Usage = 2;
    }

    internal static class Commands
    {
        private const string Help = """
            subtool — extracción, traducción y validación de subtítulos (.mkv / .mp4 / .srt)

            Comandos:
              subtool scan <ruta>
                  Analiza un video (.mkv, .mp4, .m4v) o una carpeta (recursiva) y muestra las pistas de subtítulos,
                  la pista que se elegiría y el estado de los subtítulos en inglés/español.

              subtool extract <ruta> [--out <carpeta>]
                  Extrae la pista en inglés elegida de cada video (.mkv con MKVToolNix, .mp4 con FFmpeg). Nunca sobrescribe.

              subtool validate <original.srt> <traducido.srt> [--max-warnings N]
                  Compara un SRT original con su traducción. Código de salida 1 si hay errores.

              subtool info <archivo.srt>
                  Número de bloques, duración y primeros/últimos tiempos.

              subtool translate <archivo.en.srt> [opciones]
                  Traduce al español latinoamericano con Claude → .es.srt (+ copia .srt sin idioma
                  si no existe). Reanudable: si se corta, relanza el mismo comando.
                  --backend claude-code|api  (por defecto claude-code: usa tu suscripción a través de Claude Code
                                             instalado y con sesión iniciada; 'api' usa la clave de API y se paga por uso)
                  --model claude-opus-5-5|claude-sonnet-5-5|claude-haiku-5-5 (o opus|sonnet|haiku; por defecto opus)
                  --effort low|medium|high   (por defecto medium; con claude-code también xhigh|max)
                  --chunk N                  bloques por tramo (por defecto 150)
                  --timeout MIN              tiempo máximo por llamada a Claude Code (por defecto 10)
                  --dry-run                  solo estima tokens (api: cuenta tokens y coste; claude-code:
                                             estimación local, tramos y llamadas, sin coste)
                  --restart                  descarta el progreso guardado
                  --force                    sobrescribe el .es.srt si existe
                  --no-plain-copy            no crea la copia .srt sin idioma
                  --regenerate-glossary      regenera <video>.glossary.json

              subtool glossary <archivo.en.srt> [--backend B] [--model M] [--regenerate-glossary]
                  Genera (si no existe) y muestra el glosario editable.

              subtool run <carpeta|video> [opciones de translate]
                  Escanea, extrae el inglés y traduce lo que no tenga .es.srt. Resumen por video.

              subtool key set|status|clear
                  Guarda (cifrada con DPAPI), muestra el origen (…últimos 4) o borra la clave de API
                  (solo para --backend api). La variable de entorno ANTHROPIC_API_KEY tiene prioridad.

              subtool doctor
                  Comprueba MKVToolNix, FFmpeg (solo para .mp4), Claude Code (versión y una llamada mínima con haiku)
                  y la clave de API.

              subtool version
                  Muestra la versión.

              subtool update [--check]
                  Busca una versión nueva en GitHub Releases y, si la hay, descarga el instalador, comprueba su
                  huella SHA-256 y lo ejecuta (con --check solo informa).

            Códigos de salida: 0 correcto, 1 errores de validación/extracción, 2 uso incorrecto o herramienta no encontrada.
            Variables de entorno (opcionales): SUBTOOL_MKVTOOLNIX (carpeta de MKVToolNix), SUBTOOL_FFMPEG (carpeta de FFmpeg).
            Los errores inesperados se guardan en %LOCALAPPDATA%\SubtitleTool\logs\subtitulos-AAAA-MM-DD.log.
            """;

        public static async Task<int> RunAsync(string[] args, CancellationToken ct)
        {
            if (args.Length == 0 || args[0] is "help" or "--help" or "-h" or "/?")
            {
                Console.WriteLine(Help);
                return args.Length == 0 ? ExitCodes.Usage : ExitCodes.Ok;
            }

            var rest = args.Skip(1);
            return args[0].ToLowerInvariant() switch
            {
                "scan" => await ScanAsync(ParsedArgs.Parse(rest, new HashSet<string>()), ct),
                "extract" => await ExtractAsync(ParsedArgs.Parse(rest, new HashSet<string> { "out" }), ct),
                "validate" => await ValidateAsync(ParsedArgs.Parse(rest, new HashSet<string> { "max-warnings" }), ct),
                "info" => await InfoAsync(ParsedArgs.Parse(rest, new HashSet<string>()), ct),
                "translate" => await TranslationCommands.TranslateAsync(rest, ct),
                "glossary" => await TranslationCommands.GlossaryAsync(rest, ct),
                "run" => await TranslationCommands.RunAsync(rest, ct),
                "key" => TranslationCommands.Key(rest),
                "doctor" => await TranslationCommands.DoctorAsync(rest, ct),
                "version" or "--version" => UpdateCommands.Version(),
                "update" => await UpdateCommands.UpdateAsync(rest, ct),
                _ => throw new UsageException($"Comando desconocido: '{args[0]}'."),
            };
        }

        // ---------------------------------------------------------------- scan

        private static async Task<int> ScanAsync(ParsedArgs a, CancellationToken ct)
        {
            a.RequirePositionals(1, "subtool scan <ruta>");
            var path = a.Positionals[0];
            var videos = LibraryScanner.FindVideos(path);
            if (videos.Count == 0)
            {
                Console.WriteLine($"No se encontraron videos ({VideoFormats.Description}) en: {path}");
                return ExitCodes.Ok;
            }

            var scanner = new LibraryScanner(new MediaToolkit());
            Console.WriteLine($"Analizando {videos.Count} video(s) en: {Path.GetFullPath(path)}");

            foreach (var video in videos)
            {
                var st = await scanner.ScanVideoAsync(video, null, ct);
                PrintStatus(st);
            }
            return ExitCodes.Ok;
        }

        private static void PrintStatus(VideoStatus st)
        {
            Console.WriteLine();
            Console.WriteLine($"🎬 {Path.GetFileName(st.VideoPath)}");
            if (st.Error is not null)
            {
                Console.WriteLine($"   ❌ Error al inspeccionar: {st.Error}");
                return;
            }

            if (st.SubtitleTracks.Count == 0)
            {
                Console.WriteLine("   (sin pistas de subtítulos)");
            }
            else
            {
                var chosenId = st.Selection?.Track?.Id;
                var rows = st.SubtitleTracks.Select(t => new[]
                {
                    (t.Id == chosenId ? "► " : "  ") + t.Id,
                    t.CodecId ?? t.Codec,
                    LanguageLabel(t),
                    string.IsNullOrEmpty(t.TrackName) ? "-" : t.TrackName!,
                    Flags(t),
                }).ToList();
                PrintTable(["ID", "Codec", "Idioma", "Nombre", "Flags"], rows, indent: "   ");
            }

            var sel = st.Selection;
            if (sel is { Track: { } track })
            {
                var kind = KindLabel(sel.Kind);
                Console.WriteLine($"   Pista elegida: {track.Id} ({kind})");
                if (sel.Kind == SubtitleKind.Forced)
                    Console.WriteLine("   ⚠️  Solo hay subtítulos forzados en inglés: probablemente no cubren todos los diálogos.");
            }
            else if (sel is not null)
            {
                Console.WriteLine(sel.Failure switch
                {
                    SelectionFailure.ImageOnly => "   Pista elegida: ninguna — hay inglés pero solo en imagen (PGS/VobSub): necesita OCR.",
                    _ => "   Pista elegida: ninguna — no hay subtítulos en inglés.",
                });
            }

            if (st.EnglishPath is not null)
            {
                var name = Path.GetFileName(st.EnglishPath);
                Console.WriteLine(st.EnglishExists
                    ? $"   Inglés:  ✅ ya existe  {name}"
                    : $"   Inglés:  ⬜ pendiente  {name}");
            }

            Console.WriteLine(st.SpanishExisting.Count > 0
                ? $"   Español: ✅ {string.Join(", ", st.SpanishExisting.Select(Path.GetFileName))}"
                : "   Español: ⬜ sin traducción (.es.srt / .es.sdh.srt)");

            if (st.UntaggedSrt is not null)
                Console.WriteLine($"   ℹ️  srt sin idioma: {Path.GetFileName(st.UntaggedSrt)}");
        }

        private static string LanguageLabel(MkvTrack t)
        {
            var lang = t.Language ?? "?";
            return string.IsNullOrEmpty(t.LanguageIetf) || t.LanguageIetf == lang ? lang : $"{lang} ({t.LanguageIetf})";
        }

        private static string Flags(MkvTrack t)
        {
            var flags = new List<string>();
            if (t.DefaultTrack) flags.Add("default");
            if (t.ForcedTrack) flags.Add("forced");
            if (t.HearingImpaired == true) flags.Add("SDH");
            if (TrackSelector.IsImageCodec(t)) flags.Add("imagen");
            return flags.Count == 0 ? "-" : string.Join(",", flags);
        }

        private static string KindLabel(SubtitleKind kind) => kind switch
        {
            SubtitleKind.Normal => "normal",
            SubtitleKind.Sdh => "SDH",
            SubtitleKind.Forced => "forzada",
            _ => kind.ToString(),
        };

        private static void PrintTable(string[] headers, List<string[]> rows, string indent)
        {
            var widths = headers.Select((h, i) => Math.Max(h.Length, rows.Count == 0 ? 0 : rows.Max(r => r[i].Length))).ToArray();
            string Line(IReadOnlyList<string> cells) =>
                indent + string.Join(" │ ", cells.Select((c, i) => c.PadRight(widths[i]))).TrimEnd();
            Console.WriteLine(Line(headers));
            Console.WriteLine(indent + string.Join("─┼─", widths.Select(w => new string('─', w))));
            foreach (var r in rows)
                Console.WriteLine(Line(r));
        }

        // ------------------------------------------------------------- extract

        private static async Task<int> ExtractAsync(ParsedArgs a, CancellationToken ct)
        {
            a.RequirePositionals(1, "subtool extract <ruta> [--out <carpeta>]");
            var path = a.Positionals[0];
            var outDir = a.Get("out") is { } o ? Path.GetFullPath(o) : null;

            var videos = LibraryScanner.FindVideos(path);
            if (videos.Count == 0)
            {
                Console.WriteLine($"No se encontraron videos ({VideoFormats.Description}) en: {path}");
                return ExitCodes.Ok;
            }

            var tools = new MediaToolkit();
            var scanner = new LibraryScanner(tools);
            ISubtitleTrackExtractor extractor = tools;
            int ok = 0, skipped = 0, failed = 0;

            Console.WriteLine($"Extrayendo subtítulos en inglés de {videos.Count} video(s)" +
                              (outDir is null ? " (junto a cada video)" : $" en: {outDir}"));
            Console.WriteLine();

            foreach (var video in videos)
            {
                var name = Path.GetFileName(video);
                var st = await scanner.ScanVideoAsync(video, outDir, ct);
                if (st.Error is not null)
                {
                    failed++;
                    Console.WriteLine($"❌ {name}\n   {st.Error}");
                    continue;
                }
                if (st.Selection is not { Track: { } track } sel || st.EnglishPath is null)
                {
                    failed++;
                    var reason = st.Selection?.Failure == SelectionFailure.ImageOnly
                        ? "solo hay subtítulos en inglés de imagen (PGS/VobSub/DVB): necesita OCR."
                        : "no hay subtítulos en inglés.";
                    Console.WriteLine($"❌ {name}\n   {reason}");
                    continue;
                }

                var result = await extractor.ExtractAsync(video, track.Id, st.EnglishPath, ct);
                var outName = Path.GetFileName(result.OutputPath);
                var kind = KindLabel(sel.Kind);
                switch (result.Status)
                {
                    case ExtractionStatus.Extracted:
                        ok++;
                        Console.WriteLine($"✅ {name}\n   pista {track.Id} ({kind}) → {result.OutputPath}");
                        break;
                    case ExtractionStatus.Skipped:
                        skipped++;
                        Console.WriteLine($"⏭️ {name}\n   {outName}: {result.Message}");
                        break;
                    default:
                        failed++;
                        Console.WriteLine($"❌ {name}\n   pista {track.Id}: {result.Message}");
                        break;
                }
                if (sel.Kind == SubtitleKind.Forced)
                    Console.WriteLine("   ⚠️  Pista forzada: probablemente no cubre todos los diálogos.");
            }

            Console.WriteLine();
            Console.WriteLine($"Resumen: ✅ {ok} extraído(s), ⏭️ {skipped} saltado(s), ❌ {failed} con error.");
            return failed > 0 ? ExitCodes.Failure : ExitCodes.Ok;
        }

        // ------------------------------------------------------------ validate

        private static async Task<int> ValidateAsync(ParsedArgs a, CancellationToken ct)
        {
            a.RequirePositionals(2, "subtool validate <original.srt> <traducido.srt> [--max-warnings N]");
            var maxWarnings = 20;
            if (a.Get("max-warnings") is { } mw && (!int.TryParse(mw, out maxWarnings) || maxWarnings < 0))
                throw new UsageException("--max-warnings debe ser un número entero >= 0.");

            var originalPath = a.Positionals[0];
            var translatedPath = a.Positionals[1];
            var original = await LoadSrtAsync(originalPath, ct);
            var translated = await LoadSrtAsync(translatedPath, ct);
            if (original is null || translated is null)
                return ExitCodes.Failure;

            var report = SrtValidator.Validate(original, translated);

            Console.WriteLine($"Original:  {Path.GetFileName(originalPath)} ({report.OriginalBlocks} bloques)");
            Console.WriteLine($"Traducido: {Path.GetFileName(translatedPath)} ({report.TranslatedBlocks} bloques)");
            Console.WriteLine();

            const int maxErrors = 100;
            var errors = report.Errors.ToList();
            if (errors.Count > 0)
            {
                Console.WriteLine($"❌ Errores ({errors.Count}):");
                foreach (var e in errors.Take(maxErrors))
                    Console.WriteLine($"   - {e.Message}");
                if (errors.Count > maxErrors)
                    Console.WriteLine($"   … y {errors.Count - maxErrors} más.");
                Console.WriteLine();
            }

            var warnings = report.Warnings.ToList();
            if (warnings.Count > 0)
            {
                Console.WriteLine($"⚠️  Advertencias ({warnings.Count}, se muestran {Math.Min(maxWarnings, warnings.Count)}):");
                foreach (var w in warnings.Take(maxWarnings))
                    Console.WriteLine($"   - {w.Message}");
                if (warnings.Count > maxWarnings)
                    Console.WriteLine($"   … y {warnings.Count - maxWarnings} más (usa --max-warnings N para ver más).");
                Console.WriteLine();
            }

            Console.WriteLine("Resumen:");
            Console.WriteLine($"   Bloques: original {report.OriginalBlocks}, traducido {report.TranslatedBlocks}");
            Console.WriteLine($"   Errores: {report.ErrorCount}");
            PrintKindCount(report, IssueKind.BlockCountMismatch, "número de bloques distinto");
            PrintKindCount(report, IssueKind.IndexMismatch, "índice distinto");
            PrintKindCount(report, IssueKind.TimingMismatch, "tiempos distintos");
            PrintKindCount(report, IssueKind.EmptyTranslation, "traducción vacía");
            Console.WriteLine($"   Advertencias: {report.WarningCount}");
            PrintKindCount(report, IssueKind.TagMismatch, "etiquetas distintas");
            PrintKindCount(report, IssueKind.TooManyLines, $"más de {SrtValidator.MaxLines} líneas");
            PrintKindCount(report, IssueKind.LineTooLong, $"líneas de más de {SrtValidator.MaxLineLength} caracteres");
            PrintKindCount(report, IssueKind.DialogueDashMismatch, "guion de diálogo distinto");
            Console.WriteLine();
            Console.WriteLine(report.HasErrors ? "Resultado: ❌ NO válido" : "Resultado: ✅ válido (sin errores)");

            return report.HasErrors ? ExitCodes.Failure : ExitCodes.Ok;
        }

        private static void PrintKindCount(ValidationReport report, IssueKind kind, string label)
        {
            if (report.CountsByKind.TryGetValue(kind, out var c) && c > 0)
                Console.WriteLine($"      · {label}: {c}");
        }

        private static async Task<SrtDocument?> LoadSrtAsync(string path, CancellationToken ct)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException($"No existe el archivo: {path}", path);
            try
            {
                return await SrtParser.ParseFileAsync(path, ct);
            }
            catch (SrtParseException ex)
            {
                Console.Error.WriteLine($"❌ {Path.GetFileName(path)}: {ex.Message}");
                return null;
            }
        }

        // ---------------------------------------------------------------- info

        private static async Task<int> InfoAsync(ParsedArgs a, CancellationToken ct)
        {
            a.RequirePositionals(1, "subtool info <archivo.srt>");
            var path = a.Positionals[0];
            var doc = await LoadSrtAsync(path, ct);
            if (doc is null)
                return ExitCodes.Failure;

            Console.WriteLine($"Archivo: {Path.GetFileName(path)}");
            Console.WriteLine($"Bloques: {doc.Blocks.Count}");
            if (doc.Blocks.Count == 0)
                return ExitCodes.Ok;

            var first = doc.FirstStart!.Value;
            var last = doc.LastEnd!.Value;
            Console.WriteLine($"Primer subtítulo: {Fmt(first)}");
            Console.WriteLine($"Último subtítulo termina: {Fmt(last)}");
            Console.WriteLine($"Duración cubierta: {Fmt(last - first)}");
            Console.WriteLine($"Bloques vacíos: {doc.Blocks.Count(b => b.IsEmpty)}");

            const int n = 3;
            Console.WriteLine();
            Console.WriteLine("Primeros tiempos:");
            foreach (var b in doc.Blocks.Take(n))
                Console.WriteLine($"   {b.Index,5}  {b.TimingLine.Trim()}");
            Console.WriteLine("Últimos tiempos:");
            foreach (var b in doc.Blocks.Skip(Math.Max(n, doc.Blocks.Count - n)))
                Console.WriteLine($"   {b.Index,5}  {b.TimingLine.Trim()}");
            return ExitCodes.Ok;
        }

        private static string Fmt(TimeSpan t) =>
            $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00},{t.Milliseconds:000}";
    }
}
