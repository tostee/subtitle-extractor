using SubtitleTool.App.ViewModels;
using SubtitleTool.Core.Translation;
using SubtitleTool.Core.Workflow;

namespace SubtitleTool.App.Tests;

public sealed class CubanVersionTests
{
    private const string Folder = @"C:\videos";

    [Fact]
    public async Task TranslateCuban_ProcessesRowInCubanStyle_AndKeepsSpanishColumn()
    {
        var h = new Harness();
        h.AddFolder(Folder, "a.mkv", "b.mkv");
        h.Processing.Handler = (_, _, _) => Task.FromResult(new VideoOutcome
        {
            Status = VideoOutcomeStatus.Completed, Detail = "versión cubana: 10 bloques", Style = TranslationStyle.Cuban,
        });
        var vm = h.CreateMain();
        await vm.AddPathsAsync([Folder]);
        var row = vm.Videos[1];
        var spanishBefore = row.SpanishText;

        Assert.True(vm.TranslateCubanCommand.CanExecute(row));
        await vm.TranslateCubanCommand.ExecuteAsync(row);

        Assert.Equal(["b.mkv"], h.Processing.Processed);
        var settings = Assert.Single(h.Processing.SettingsSeen);
        Assert.Equal(TranslationStyle.Cuban, settings.Style);
        Assert.False(settings.Force);
        Assert.Equal(spanishBefore, row.SpanishText); // la columna Español es de la traducción normal
        Assert.Equal("versión cubana lista", row.ProgressText);
        Assert.Empty(h.Dialogs.Summaries);

        // Procesar todo después vuelve al estilo normal.
        h.Processing.Handler = null;
        await vm.ProcessAllCommand.ExecuteAsync(null);
        Assert.All(h.Processing.SettingsSeen.Skip(1), s => Assert.Equal(TranslationStyle.Neutral, s.Style));
    }

    [Fact]
    public void TranslateCuban_NotAvailableWithoutEnglishSrt()
    {
        var row = new VideoItemViewModel(@"C:\videos\x.mkv");
        Assert.False(row.CanTranslateCuban); // sin analizar todavía
    }
}
