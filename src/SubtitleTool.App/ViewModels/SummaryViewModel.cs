namespace SubtitleTool.App.ViewModels;

/// <summary>Ventana «Resumen del procesamiento»: una fila por video y los totales por estado.</summary>
public sealed class SummaryViewModel
{
    /// <summary>Orden de los estados en los totales y en la leyenda.</summary>
    public static IReadOnlyList<StatusKind> LegendKinds { get; } =
        [StatusKind.Success, StatusKind.Review, StatusKind.Skipped, StatusKind.Cancelled, StatusKind.Error];

    public SummaryViewModel(IReadOnlyList<SummaryRow> rows)
    {
        Rows = rows;
        TotalsText = BuildTotals(rows);
    }

    public IReadOnlyList<SummaryRow> Rows { get; }

    /// <summary>Leyenda: icono y palabra de cada estado posible.</summary>
    public IReadOnlyList<LegendItem> Legend { get; } =
        LegendKinds.Select(k => new LegendItem(k, k == StatusKind.Error ? "error" : StatusKinds.Label(k))).ToList();

    /// <summary>P. ej. «4 videos: 2 hechos · 1 por revisar · 1 con error».</summary>
    public string TotalsText { get; }

    /// <summary>Hay traducciones esperando revisión (se explica cómo abrirlas).</summary>
    public bool HasPendingReview => Rows.Any(r => r.Kind == StatusKind.Review);

    private static string BuildTotals(IReadOnlyList<SummaryRow> rows)
    {
        var parts = rows.GroupBy(r => r.Kind)
            .OrderBy(g => Order(g.Key))
            .Select(g => $"{g.Count()} {Plural(g.First().StatusLabel, g.Count())}");
        return $"{rows.Count} {(rows.Count == 1 ? "video" : "videos")}: " + string.Join(" · ", parts);
    }

    private static int Order(StatusKind kind)
    {
        var i = LegendKinds.ToList().IndexOf(kind);
        return i < 0 ? int.MaxValue : i;
    }

    private static string Plural(string label, int count) => (label, count) switch
    {
        ("hecho", > 1) => "hechos",
        ("saltado", > 1) => "saltados",
        ("cancelado", > 1) => "cancelados",
        _ => label,
    };
}

/// <summary>Entrada de la leyenda del resumen.</summary>
public sealed record LegendItem(StatusKind Kind, string Label);
