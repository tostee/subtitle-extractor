using System.Globalization;
using SubtitleTool.Core.Srt;
using SubtitleTool.Core.Translation;

namespace SubtitleTool.Core.Review;

public sealed class ReviewException : Exception
{
    public ReviewException(string message, bool isStaleDraft = false) : base(message) => IsStaleDraft = isStaleDraft;

    /// <summary>Hay un borrador pero ya no encaja con el inglés: solo se puede descartar.</summary>
    public bool IsStaleDraft { get; }
}

/// <summary>Qué se está revisando.</summary>
public enum ReviewSource
{
    /// <summary>Borrador recién traducido (el .es.srt se crea, o se reemplaza si se tradujo con --force, al guardar).</summary>
    Draft,
    /// <summary>Un .es.srt ya guardado: al guardar se sobrescribe dejando antes una copia .bak.</summary>
    SavedFile,
}

/// <summary>Un bloque: inglés y español lado a lado. <see cref="Id"/> es la posición 1-based.</summary>
public sealed class ReviewBlock
{
    public required int Id { get; init; }
    public required int Index { get; init; }
    public required TimeSpan Start { get; init; }
    public required TimeSpan End { get; init; }
    public required string English { get; init; }
    /// <summary>Español tal como se cargó (para saber si se editó).</summary>
    public required string LoadedSpanish { get; init; }
    public string Spanish { get; set; } = "";
    /// <summary>El original está vacío: no se traduce ni se edita.</summary>
    public bool IsEmptyOriginal { get; init; }
    /// <summary>Ya se había editado en una sesión anterior (borrador guardado).</summary>
    public bool EditedBefore { get; init; }

    public bool IsEdited => EditedBefore || !string.Equals(Normalize(Spanish), Normalize(LoadedSpanish), StringComparison.Ordinal);

    public string TimingText => $"{Format(Start)} → {Format(End)}";

    private static string Format(TimeSpan t) => t.ToString(@"hh\:mm\:ss\,fff", CultureInfo.InvariantCulture);
    internal static string Normalize(string text) => string.Join("\n", SubtitleTranslator.NormalizeLines(text));
}

/// <summary>Sesión de revisión de una traducción (borrador o .es.srt guardado).</summary>
public sealed class ReviewSession
{
    public required TranslationPaths Paths { get; init; }
    public required ReviewSource Source { get; init; }
    public required SrtDocument English { get; init; }
    /// <summary>El español tal como estaba en disco (para conservar sus tiempos al guardar un .es.srt existente).</summary>
    public required SrtDocument LoadedSpanish { get; init; }
    public required IReadOnlyList<ReviewBlock> Blocks { get; init; }
    /// <summary>Crear la copia .srt sin idioma al guardar un borrador.</summary>
    public bool CreatePlainCopy { get; init; }
    /// <summary>Borrador hecho con --force sobre un .es.srt existente: al guardar lo reemplaza (con copia .bak).</summary>
    public bool ReplacesExisting { get; init; }

    /// <summary>Archivo de destino al guardar.</summary>
    public string TargetPath => Paths.SpanishPath;

    /// <summary>Al guardar se sobrescribe un archivo que ya existe (se pide confirmación y se deja copia).</summary>
    public bool OverwritesExisting => Source == ReviewSource.SavedFile || (ReplacesExisting && File.Exists(TargetPath));

    /// <summary>
    /// Documento a guardar. Un borrador toma índices y tiempos del inglés (como la traducción); un .es.srt ya guardado
    /// conserva los suyos (puede estar resincronizado) y solo cambia el texto.
    /// </summary>
    public SrtDocument BuildSpanish()
    {
        if (Source == ReviewSource.Draft)
            return SubtitleTranslator.Assemble(English, Blocks.Where(b => !b.IsEmptyOriginal).ToDictionary(b => b.Id, b => b.Spanish));
        var doc = new SrtDocument();
        for (var i = 0; i < LoadedSpanish.Blocks.Count; i++)
        {
            var s = LoadedSpanish.Blocks[i];
            doc.Blocks.Add(new SrtBlock
            {
                Index = s.Index, TimingLine = s.TimingLine, Start = s.Start, End = s.End,
                Lines = Blocks[i].IsEmptyOriginal ? s.Lines.ToList() : SubtitleTranslator.NormalizeLines(Blocks[i].Spanish),
            });
        }
        return doc;
    }

    /// <summary>Validación contra el inglés. En un .es.srt ya guardado no se exige que índices y tiempos coincidan.</summary>
    public ValidationReport Validate()
    {
        var built = BuildSpanish();
        var report = SrtValidator.Validate(English, built);
        if (Source == ReviewSource.Draft)
            return report;
        var filtered = new ValidationReport { OriginalBlocks = report.OriginalBlocks, TranslatedBlocks = report.TranslatedBlocks };
        filtered.Issues.AddRange(report.Issues.Where(i => i.Kind is not (IssueKind.TimingMismatch or IssueKind.IndexMismatch)));
        return filtered;
    }

    public IEnumerable<int> EditedIds => Blocks.Where(b => b.IsEdited).Select(b => b.Id);
}

public sealed record ReviewSaveResult(string SpanishPath, string? BackupPath, PlainCopyStatus PlainCopy, ValidationReport Report);

/// <summary>Abre, guarda y descarta revisiones. Lo usan la ventana y <c>subtool review</c>.</summary>
public sealed class ReviewService
{
    private readonly ReviewStore _store;
    private readonly Func<DateTime> _now;

    public ReviewService(ReviewStore? store = null, Func<DateTime>? now = null)
    {
        _store = store ?? ReviewStore.Default;
        _now = now ?? (() => DateTime.Now);
    }

    public ReviewStore Store => _store;

    /// <summary>Hay un borrador (aunque no encaje: así se puede descartar).</summary>
    public bool HasDraft(string? englishPath) => _store.HasDraft(englishPath);

    /// <summary>Abre el borrador pendiente o, si no hay, el .es.srt guardado.</summary>
    /// <exception cref="ReviewException">No hay nada que revisar, el borrador ya no encaja con el inglés
    /// (<see cref="ReviewException.IsStaleDraft"/>) o el español no encaja bloque a bloque.</exception>
    public async Task<ReviewSession> OpenAsync(string englishPath, CancellationToken ct = default)
    {
        var paths = TranslationPaths.FromEnglish(englishPath);

        ReviewSource source;
        string spanishFile;
        ReviewDraftInfo? info = null;
        switch (_store.GetState(paths.EnglishPath))
        {
            case DraftState.Stale:
                throw new ReviewException(
                    $"El subtítulo en inglés ({Path.GetFileName(paths.EnglishPath)}) cambió o ya no existe desde que se tradujo: " +
                    "el borrador pendiente de revisión ya no encaja. Hay que descartarlo y volver a traducir.", isStaleDraft: true);
            case DraftState.Valid:
                source = ReviewSource.Draft;
                spanishFile = _store.DraftPath(paths.EnglishPath);
                info = _store.LoadInfo(paths.EnglishPath);
                break;
            default:
                if (!File.Exists(paths.EnglishPath))
                    throw new ReviewException($"No existe el subtítulo en inglés: {paths.EnglishPath}");
                if (!File.Exists(paths.SpanishPath))
                    throw new ReviewException($"No hay traducción que revisar para {Path.GetFileName(paths.EnglishPath)}.");
                source = ReviewSource.SavedFile;
                spanishFile = paths.SpanishPath;
                break;
        }

        var english = await SrtParser.ParseFileAsync(paths.EnglishPath, ct).ConfigureAwait(false);
        var spanish = await SrtParser.ParseFileAsync(spanishFile, ct).ConfigureAwait(false);
        if (spanish.Blocks.Count != english.Blocks.Count)
        {
            throw new ReviewException(
                $"{Path.GetFileName(spanishFile)} tiene {spanish.Blocks.Count} bloques y el inglés {english.Blocks.Count}: " +
                "no se pueden revisar lado a lado.", isStaleDraft: source == ReviewSource.Draft);
        }

        var editedBefore = info?.Edited.ToHashSet() ?? [];
        var blocks = english.Blocks.Select((e, i) =>
        {
            var es = string.Join("\n", spanish.Blocks[i].Lines);
            return new ReviewBlock
            {
                Id = i + 1,
                Index = e.Index,
                Start = e.Start,
                End = e.End,
                English = string.Join("\n", e.Lines),
                LoadedSpanish = es,
                Spanish = es,
                IsEmptyOriginal = e.IsEmpty,
                EditedBefore = editedBefore.Contains(i + 1),
            };
        }).ToList();

        return new ReviewSession
        {
            Paths = paths,
            Source = source,
            English = english,
            LoadedSpanish = spanish,
            Blocks = blocks,
            CreatePlainCopy = info?.CreatePlainCopy ?? false,
            ReplacesExisting = info?.ReplacesExisting ?? false,
        };
    }

    /// <summary>Guarda los cambios en el borrador (sin crear el .es.srt) para seguir más tarde.</summary>
    public Task SaveDraftAsync(ReviewSession session, CancellationToken ct = default) =>
        session.Source == ReviewSource.Draft
            ? _store.SaveDraftAsync(session.Paths, session.BuildSpanish(), session.CreatePlainCopy, session.EditedIds, ct: ct)
            : throw new ReviewException("Solo se puede guardar como borrador una traducción pendiente de revisión.");

    /// <summary>
    /// Valida y escribe el .es.srt. Un borrador normal nunca sobrescribe un .es.srt existente; un .es.srt ya guardado
    /// (o un borrador hecho con --force) se sobrescribe dejando antes una copia <c>.AAAAMMDD-HHMMSS.bak</c>.
    /// </summary>
    /// <exception cref="ReviewException">Errores de validación (p. ej. un bloque vacío) o el destino ya existe.</exception>
    public async Task<ReviewSaveResult> SaveAsync(ReviewSession session, CancellationToken ct = default)
    {
        var translated = session.BuildSpanish();
        var report = session.Validate();
        if (report.HasErrors)
        {
            var first = report.Errors.Take(5).Select(e => e.Message);
            throw new ReviewException($"No se puede guardar: {report.ErrorCount} error(es).\n" + string.Join("\n", first));
        }

        var target = session.TargetPath;
        var overwrite = session.OverwritesExisting;
        if (!overwrite && File.Exists(target))
            throw new ReviewException($"Ya existe {Path.GetFileName(target)}; no se sobrescribe. Bórralo o descarta este borrador.");

        string? backup = null;
        if (overwrite && File.Exists(target))
        {
            backup = BackupPathFor(target);
            File.Copy(target, backup, overwrite: false);
        }

        var tmp = target + ".tmp";
        await SrtWriter.WriteFileAsync(tmp, translated, ct).ConfigureAwait(false);
        File.Move(tmp, target, overwrite);

        var plain = PlainCopyStatus.NotAttempted;
        if (session.Source == ReviewSource.Draft)
        {
            // El .es.srt ya está escrito: el borrador sobra pase lo que pase con la copia sin idioma.
            _store.Delete(session.Paths.EnglishPath);
            plain = session.CreatePlainCopy ? await WritePlainCopyAsync(session.Paths.PlainCopyPath, translated, ct).ConfigureAwait(false)
                                            : PlainCopyStatus.Disabled;
        }
        return new ReviewSaveResult(target, backup, plain, report);
    }

    /// <summary>Copia con fecha y hora: nunca pisa una copia anterior (ni un .bak que ya tuviera el usuario).</summary>
    private string BackupPathFor(string target)
    {
        var stamp = _now().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var path = $"{target}.{stamp}.bak";
        for (var n = 2; File.Exists(path); n++)
            path = $"{target}.{stamp}-{n}.bak";
        return path;
    }

    private static async Task<PlainCopyStatus> WritePlainCopyAsync(string path, SrtDocument doc, CancellationToken ct)
    {
        if (File.Exists(path))
            return PlainCopyStatus.SkippedExists;
        try
        {
            var tmp = path + ".tmp";
            await SrtWriter.WriteFileAsync(tmp, doc, ct).ConfigureAwait(false);
            File.Move(tmp, path, overwrite: false);
            return PlainCopyStatus.Created;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return PlainCopyStatus.Failed;
        }
    }

    /// <summary>Borra el borrador (habrá que volver a traducir).</summary>
    public void Discard(string englishPath) => _store.Delete(TranslationPaths.FromEnglish(englishPath).EnglishPath);
}
