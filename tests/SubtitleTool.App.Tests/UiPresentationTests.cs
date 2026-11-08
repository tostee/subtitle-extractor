using System.Windows;
using SubtitleTool.App.Themes;
using SubtitleTool.App.ViewModels;
using SubtitleTool.App.Views;
using SubtitleTool.Core.Workflow;

namespace SubtitleTool.App.Tests;

/// <summary>Lógica de presentación sin ventanas: acciones disponibles, resumen, colores de estado y alto contraste.</summary>
public sealed class UiPresentationTests
{
    private const string Folder = @"X:\Pelis";

    [Fact]
    public async Task RowActions_AreEnabledOnlyWhenTheyCanDoSomething()
    {
        var h = new Harness();
        h.AddFolder(Folder, "a.mkv", "b.mkv");
        var started = new TaskCompletionSource();
        h.Processing.Handler = async (st, _, ct) =>
        {
            if (Path.GetFileName(st.VideoPath) == "a.mkv")
            {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }
            return new VideoOutcome { Status = VideoOutcomeStatus.Failed, Detail = "falló (prueba)" };
        };
        var vm = h.CreateMain();
        await vm.AddPathsAsync([Folder]);
        var (a, b) = (vm.Videos[0], vm.Videos[1]);

        Assert.False(vm.ProcessOneCommand.CanExecute(null));
        Assert.True(vm.ProcessOneCommand.CanExecute(a));
        Assert.False(vm.RetryCommand.CanExecute(a));          // no ha fallado

        var run = vm.ProcessOneCommand.ExecuteAsync(a);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(vm.ProcessOneCommand.CanExecute(a));     // en proceso
        Assert.True(vm.ProcessOneCommand.CanExecute(b));      // se puede añadir otro a la cola en marcha

        Assert.True(vm.CancelCommand.CanExecute(null));
        vm.CancelCommand.Execute(null);
        Assert.False(vm.CancelCommand.CanExecute(null));      // ya se pidió: no se ofrece otra vez
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(a.HasFailed);                             // cancelado
        Assert.True(vm.RetryCommand.CanExecute(a));
        Assert.False(vm.CancelCommand.CanExecute(null));
    }

    [Fact]
    public async Task RowActions_AreRequeried_WhenARowChangesState_OrTheQueueIsCancelling()
    {
        var h = new Harness();
        h.AddFolder(Folder, "a.mkv");
        var vm = h.CreateMain();
        await vm.AddPathsAsync([Folder]);
        var a = vm.Videos[0];
        var (processOne, retry) = (0, 0);
        vm.ProcessOneCommand.CanExecuteChanged += (_, _) => processOne++;
        vm.RetryCommand.CanExecuteChanged += (_, _) => retry++;

        a.State = RowState.Failed;                            // el menú contextual se vuelve a evaluar
        Assert.Equal(1, processOne);
        Assert.Equal(1, retry);
        Assert.True(vm.RetryCommand.CanExecute(a));

        vm.IsCancelling = true;                               // mientras se cancela no se añaden videos
        Assert.Equal(2, processOne);
        Assert.False(vm.ProcessOneCommand.CanExecute(a));
        Assert.False(vm.RetryCommand.CanExecute(a));
        vm.IsCancelling = false;
        Assert.True(vm.ProcessOneCommand.CanExecute(a));
    }

    [Fact]
    public void ReleaseNotes_AreShownAsProse_AndRowLogAsRegistro()
    {
        var h = new Harness();
        h.Updates.Newer = FakeUpdates.Release("9.9.9");
        var vm = h.CreateMain();
        vm.AvailableUpdate = h.Updates.Newer;

        vm.ViewReleaseNotesCommand.Execute(null);
        Assert.True(Assert.Single(h.Dialogs.TextProse));

        vm.ViewLogCommand.Execute(new VideoItemViewModel(Path.Combine(Folder, "a.mkv")));
        Assert.StartsWith("Registro — a.mkv", h.Dialogs.Texts[1].Title);
        Assert.False(h.Dialogs.TextProse[1]);
    }

    [Fact]
    public void Summary_ShowsTotals_AndStatusInWords()
    {
        var rows = new List<SummaryRow>
        {
            new("a.mkv", SummaryRow.KindFor(VideoOutcomeStatus.Completed), "ok"),
            new("b.mkv", SummaryRow.KindFor(VideoOutcomeStatus.Completed), "ok"),
            new("c.mkv", SummaryRow.KindFor(VideoOutcomeStatus.PendingReview), "lista para revisar"),
            new("d.mkv", SummaryRow.KindFor(VideoOutcomeStatus.Failed), "error"),
        };
        var vm = new SummaryViewModel(rows);

        Assert.Equal("4 videos: 2 hechos · 1 por revisar · 1 con error", vm.TotalsText);
        Assert.True(vm.HasPendingReview);
        Assert.Equal("por revisar", rows[2].StatusLabel);
        Assert.Equal(StatusKind.Review, rows[2].Kind);
        Assert.Equal("cancelado", new SummaryRow("e", SummaryRow.KindFor(VideoOutcomeStatus.Cancelled), "").StatusLabel);
        Assert.False(new SummaryViewModel([rows[0]]).HasPendingReview);
        Assert.Equal("1 video: 1 hecho", new SummaryViewModel([rows[0]]).TotalsText);
    }

    [Fact]
    public void EveryStatusKind_HasAGlyph_APaletteColor_AndAWord()
    {
        var paletteKeys = HighContrastPalette.Map.Select(m => m.Key).ToHashSet();
        foreach (var kind in Enum.GetValues<StatusKind>().Where(k => k != StatusKind.None))
        {
            var glyph = StatusIcons.Glyph(kind);
            Assert.Single(glyph);
            Assert.InRange(glyph[0], '\uE000', '\uF8FF');              // zona privada de Segoe Fluent Icons / MDL2
            Assert.Contains(StatusIcons.BrushKey(kind), paletteKeys);     // color que sigue al alto contraste
            Assert.NotEqual("", StatusKinds.Label(kind));                // el icono nunca va solo
        }
        Assert.Equal("", StatusIcons.Glyph(StatusKind.None));
        Assert.Equal("\uE73E", StatusIcons.Glyph(StatusKind.Success));
        Assert.Equal("\uEA39", StatusIcons.Glyph(StatusKind.Error));
        Assert.Equal("\uE7BA", StatusIcons.Glyph(StatusKind.Warning));
        Assert.Equal("ErrorText", StatusIcons.BrushKey(StatusKind.Error));
        Assert.Equal("WarningText", StatusIcons.BrushKey(StatusKind.Review));   // espera tu acción: no es «en curso»
        Assert.Equal("InfoText", StatusIcons.BrushKey(StatusKind.InProgress));
    }

    [Theory]
    [InlineData(VideoItemViewModel.ExistsText, StatusKind.Success)]
    [InlineData(VideoItemViewModel.ReviewText, StatusKind.Review)]
    [InlineData(VideoItemViewModel.PendingText, StatusKind.Pending)]
    [InlineData(VideoItemViewModel.InProgressText, StatusKind.InProgress)]
    [InlineData(VideoItemViewModel.ErrorText, StatusKind.Error)]
    [InlineData(VideoItemViewModel.UnavailableText, StatusKind.Error)]
    [InlineData("—", StatusKind.None)]
    [InlineData(null, StatusKind.None)]
    public void RowCells_MapTextToStatusKind(string? text, StatusKind kind) =>
        Assert.Equal(kind, VideoItemViewModel.KindForCell(text));

    [Fact]
    public void Summary_KindForEachOutcome()
    {
        Assert.Equal(StatusKind.Success, SummaryRow.KindFor(VideoOutcomeStatus.Completed));
        Assert.Equal(StatusKind.Skipped, SummaryRow.KindFor(VideoOutcomeStatus.Skipped));
        Assert.Equal(StatusKind.Cancelled, SummaryRow.KindFor(VideoOutcomeStatus.Cancelled));
        Assert.Equal(StatusKind.Review, SummaryRow.KindFor(VideoOutcomeStatus.PendingReview));
        Assert.Equal(StatusKind.Error, SummaryRow.KindFor(VideoOutcomeStatus.Failed));
        Assert.Equal(["hecho", "por revisar", "saltado", "cancelado", "error"], new SummaryViewModel([]).Legend.Select(l => l.Label));
    }

    [Fact]
    public async Task Log_KeepsTheTypeApart_AndNoTextCarriesEmoji()
    {
        var h = new Harness();
        h.AddFolder(Folder, "a.mkv", "b.mkv");
        h.Library.MkvMissing = true;
        h.Library.FfmpegDetected = null;
        h.Processing.Handler = (_, _, _) => Task.FromResult(new VideoOutcome { Status = VideoOutcomeStatus.Completed, Detail = "10 bloques" });
        var vm = h.CreateMain();
        vm.ReportToolStatus();
        await vm.AddPathsAsync([Folder]);
        await vm.ProcessAllCommand.ExecuteAsync(null);

        Assert.Equal(StatusKind.Warning, vm.LogLines.First(l => l.Text.Contains("No se encontró MKVToolNix")).Kind);
        Assert.Equal(StatusKind.Info, vm.LogLines.First(l => l.Text.Contains("No se encontró FFmpeg")).Kind);
        Assert.Contains(vm.LogLines, l => l.Kind == StatusKind.Error);
        Assert.All(vm.LogLines, l => Assert.False(HasEmoji(l.Text), l.Text));
        Assert.All(vm.Videos.SelectMany(v => v.LogLines), l => Assert.False(HasEmoji(l), l));
        Assert.All(h.ActivityLog.Lines, l => Assert.False(HasEmoji(l), l));
        // En el archivo de registro (texto plano) el tipo va en palabras.
        Assert.Contains(h.ActivityLog.Lines, l => l.StartsWith("[aviso] No se encontró MKVToolNix", StringComparison.Ordinal));
        Assert.Equal("[error] x", new LogEntry(StatusKind.Error, "x").SpokenText);
    }

    private static bool HasEmoji(string text) =>
        text.EnumerateRunes().Any(r => r.Value is >= 0x2600 and <= 0x27BF or >= 0x1F000 or 0xFE0F or 0x2139 or >= 0x23E9 and <= 0x23FA or 0x2B06);

    [Fact]
    public void HighContrast_OverridesEveryPaletteKey_AndRestoresThem()
    {
        var resources = new ResourceDictionary();
        resources.MergedDictionaries.Add(new ResourceDictionary { ["Text"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Red) });
        HighContrastPalette.Apply(resources, highContrast: true);

        var hc = Assert.IsType<HighContrastPalette.HighContrastDictionary>(resources.MergedDictionaries[^1]);   // por encima de Styles.xaml
        Assert.Equal(HighContrastPalette.Map.Count, hc.Count);
        Assert.Equal(SystemColors.WindowTextBrush.Color, ((System.Windows.Media.SolidColorBrush)resources["Text"]).Color);
        foreach (var key in new[] { "SuccessText", "ErrorText", "InfoText", "WarningText", "Text" })
            Assert.True(resources.Contains(key), key);

        HighContrastPalette.Apply(resources, highContrast: false);
        Assert.Empty(Assert.IsType<HighContrastPalette.HighContrastDictionary>(resources.MergedDictionaries[^1]));   // vacío: manda la paleta propia
        Assert.Equal(System.Windows.Media.Colors.Red, ((System.Windows.Media.SolidColorBrush)resources["Text"]).Color);
    }

    [Fact]
    public void HighContrast_OnlyChangesWhenSomethingChanged()
    {
        var resources = new ResourceDictionary();
        HighContrastPalette.Apply(resources, highContrast: false);   // sin alto contraste ni sustituciones: nada
        Assert.Empty(resources.MergedDictionaries);

        HighContrastPalette.Apply(resources, highContrast: true);
        var first = resources.MergedDictionaries.Single();
        HighContrastPalette.Apply(resources, highContrast: true);    // mismos colores de sistema: no se toca
        Assert.Same(first, resources.MergedDictionaries.Single());

        HighContrastPalette.Apply(resources, highContrast: false);
        var emptied = resources.MergedDictionaries.Single();
        HighContrastPalette.Apply(resources, highContrast: false);   // ya sin alto contraste: no se toca
        Assert.Same(emptied, resources.MergedDictionaries.Single());
    }

    /// <summary>Todo color de la paleta debe tener sustituto en alto contraste (si no, quedaría fijo con el tema del sistema).</summary>
    [Fact]
    public void HighContrast_CoversEveryBrushInStyles()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SubtitleTool.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var styles = System.Xml.Linq.XDocument.Load(Path.Combine(dir!.FullName, "src", "SubtitleTool.App", "Themes", "Styles.xaml"));
        System.Xml.Linq.XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var brushKeys = styles.Root!.Elements().Where(e => e.Name.LocalName == "SolidColorBrush")
            .Select(e => (string?)e.Attribute(x + "Key")).OfType<string>().ToList();

        Assert.NotEmpty(brushKeys);
        Assert.Equal(brushKeys.Order(), HighContrastPalette.Map.Select(m => m.Key).Order());
    }
}
