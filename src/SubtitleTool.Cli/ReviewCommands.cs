using SubtitleTool.Core.Review;
using SubtitleTool.Core.Srt;
using SubtitleTool.Core.Translation;

namespace SubtitleTool.Cli;

/// <summary><c>subtool review &lt;archivo.en.srt&gt; [--accept | --discard]</c>.</summary>
internal static class ReviewCommands
{
    public static async Task<int> RunAsync(IEnumerable<string> args, CancellationToken ct)
    {
        var a = ParsedArgs.Parse(args, new HashSet<string>(), new HashSet<string> { "accept", "discard" });
        a.RequirePositionals(1, "subtool review <archivo.en.srt> [--accept | --discard]");
        if (a.Has("accept") && a.Has("discard"))
            throw new UsageException("Usa --accept o --discard, no los dos.");

        var paths = TranslationPaths.FromEnglish(a.Positionals[0]);
        var service = new ReviewService();
        if (!service.HasDraft(paths.EnglishPath))
        {
            Console.WriteLine($"No hay ninguna traducción pendiente de revisión para {Path.GetFileName(paths.EnglishPath)}.");
            return ExitCodes.Failure;
        }

        if (a.Has("discard"))
        {
            service.Discard(paths.EnglishPath);
            Console.WriteLine("🗑️  Borrador descartado. Vuelve a traducir para generar otro.");
            return ExitCodes.Ok;
        }

        ReviewSession session;
        try
        {
            session = await service.OpenAsync(paths.EnglishPath, ct);
        }
        catch (ReviewException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            if (ex.IsStaleDraft)
                Console.Error.WriteLine("   Descártalo con --discard y vuelve a traducir.");
            return ExitCodes.Failure;
        }

        var report = session.Validate();
        var edited = session.Blocks.Count(b => b.IsEdited);
        Console.WriteLine($"📝 {Path.GetFileName(paths.EnglishPath)} → {Path.GetFileName(paths.SpanishPath)}");
        Console.WriteLine($"   {session.Blocks.Count} bloques · {edited} editado(s) · {report.ErrorCount} error(es) · {report.WarningCount} aviso(s) de estilo");
        foreach (var w in report.Issues.Take(15))
            Console.WriteLine($"   {(w.Severity == IssueSeverity.Error ? "❌" : "⚠️")} {w.Message}");
        if (report.Issues.Count > 15)
            Console.WriteLine($"   … y {report.Issues.Count - 15} más.");

        if (!a.Has("accept"))
        {
            Console.WriteLine("   Revísala en la ventana (clic derecho → «Revisar traducción…»), o guárdala tal cual con --accept.");
            return ExitCodes.Ok;
        }

        try
        {
            var r = await service.SaveAsync(session, ct);
            Console.WriteLine($"✅ Guardado: {r.SpanishPath}");
            if (r.BackupPath is not null)
                Console.WriteLine($"   Copia del anterior: {r.BackupPath}");
            if (r.PlainCopy == PlainCopyStatus.Created)
                Console.WriteLine($"   Copia sin idioma creada: {paths.PlainCopyPath}");
            else if (r.PlainCopy == PlainCopyStatus.Failed)
                Console.WriteLine($"   ⚠️  No se pudo crear la copia sin idioma {paths.PlainCopyPath}.");
            return ExitCodes.Ok;
        }
        catch (ReviewException ex)
        {
            Console.Error.WriteLine($"❌ {ex.Message}");
            return ExitCodes.Failure;
        }
    }
}
