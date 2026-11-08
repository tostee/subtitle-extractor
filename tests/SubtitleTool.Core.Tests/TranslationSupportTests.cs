using SubtitleTool.Core.Translation;

namespace SubtitleTool.Core.Tests;

public class TranslationSupportTests
{
    [Theory]
    [InlineData(@"D:\Pelis\Movie (2026).en.srt", @"D:\Pelis\Movie (2026).es.srt", @"D:\Pelis\Movie (2026).srt", @"D:\Pelis\Movie (2026).glossary.json", false)]
    [InlineData(@"D:\Pelis\Movie (2026).en.sdh.srt", @"D:\Pelis\Movie (2026).es.sdh.srt", @"D:\Pelis\Movie (2026).srt", @"D:\Pelis\Movie (2026).glossary.json", true)]
    [InlineData(@"D:\Pelis\A.B.EN.SRT", @"D:\Pelis\A.B.es.srt", @"D:\Pelis\A.B.srt", @"D:\Pelis\A.B.glossary.json", false)]
    public void Paths_FromEnglish(string en, string es, string plain, string glossary, bool sdh)
    {
        var p = TranslationPaths.FromEnglish(en);

        Assert.Equal(es, p.SpanishPath);
        Assert.Equal(plain, p.PlainCopyPath);
        Assert.Equal(glossary, p.GlossaryPath);
        Assert.Equal(sdh, p.IsSdh);
    }

    [Theory]
    [InlineData(@"D:\Pelis\Movie.srt")]
    [InlineData(@"D:\Pelis\Movie.es.srt")]
    [InlineData(@"D:\Pelis\Movie.en.ass")]
    public void Paths_RejectNonEnglishSrt(string path)
    {
        Assert.False(TranslationPaths.TryFromEnglish(path, out _));
        Assert.Throws<ArgumentException>(() => TranslationPaths.FromEnglish(path));
    }

    [Fact]
    public void CostEstimate_KnownNumbers_Opus()
    {
        // Sistema 1000 tokens; 2 tramos de 5000 tokens de entrada (incl. sistema); 2000 tokens de inglés; glosario ya existe.
        // Tramo 1: 4000×$4 + 1000×$5 (escritura caché) = 21000 µ$·MTok; tramo 2: 4000×$4 + 1000×$0,20 = 16200.
        // Entrada = 37200 / 1e6 = $0,0372. Salida = ceil(2000×1,3)=2600 × $20 / 1e6 = $0,052.
        var e = CostEstimator.Estimate("claude-opus-5-5", 1000, [5000, 5000], 2000, 0);

        Assert.Equal(0.0372m, e.InputCostUsd);
        Assert.Equal(2600, e.EstimatedOutputTokens);
        Assert.Equal(0.052m, e.OutputCostUsd);
        Assert.Equal(0.0892m, e.TotalCostUsd);
        Assert.Equal(10000, e.TotalInputTokens);
    }

    [Fact]
    public void CostEstimate_WithGlossary_Haiku()
    {
        // Glosario 10000 tokens × $0,10 = $0,001; salida 1300 + 3000 = 4300 × $0,50 / 1e6 = $0,00215.
        // Tramo único: 0 sin caché + 1000 × $0,125 (escritura) = $0,000125.
        var e = CostEstimator.Estimate("claude-haiku-5-5", 1000, [1000], 1000, 10000);

        Assert.Equal(0.001125m, e.InputCostUsd);
        Assert.Equal(4300, e.EstimatedOutputTokens);
        Assert.Equal(0.00215m, e.OutputCostUsd);
    }

    [Fact]
    public void Pricing_CostOfUsage()
    {
        var p = ModelPricing.For("claude-sonnet-5-5");
        // 1M entrada × $2 + 1M salida × $10 + 1M caché leída × $0,10 + 1M caché escrita × $2,50
        Assert.Equal(14.6m, p.Cost(new TokenUsage(1_000_000, 1_000_000, 1_000_000, 1_000_000)));
        Assert.Equal(0.20m, ModelPricing.For("claude-opus-5-5").CacheReadPerMTok);
        Assert.Throws<ArgumentException>(() => ModelPricing.For("gpt-4"));
    }

    [Fact]
    public void ParseItems_ValidAndInvalid()
    {
        var ok = TranslationPrompts.ParseItems("""{"items":[{"id":1,"text":"Hola"},{"id":2,"text":"- A\n- B"}]}""");
        Assert.NotNull(ok);
        Assert.Equal(2, ok!.Count);
        Assert.Equal("- A\n- B", ok[1].Text);

        Assert.Null(TranslationPrompts.ParseItems("""{"items":[{"id":1,"text":"Hola"}"""));
        Assert.Null(TranslationPrompts.ParseItems("""{"x":[]}"""));
        Assert.Null(TranslationPrompts.ParseItems("""{"items":[{"id":"1","text":"Hola"}]}"""));
        Assert.Null(TranslationPrompts.ParseItems(""));
    }

    [Fact]
    public void Ranges_Compact()
    {
        var items = new[] { 1, 2, 3, 5, 7, 8 }.Select(i => new TranslationItem(i, "")).ToList();
        Assert.Equal("1-3,5,7-8", SubtitleTranslator.Ranges(items));
    }

    [Fact]
    public void ApiKeyStore_EnvironmentHasPriority_AndFileIsEncrypted()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var dir = Path.Combine(Path.GetTempPath(), "subtool-key-" + Guid.NewGuid().ToString("N"));
        try
        {
            var file = Path.Combine(dir, "apikey.bin");
            var noEnv = new ApiKeyStore(file, _ => null);
            Assert.Equal(ApiKeySource.None, noEnv.Resolve().Source);

            noEnv.Save("sk-ant-test-1234567890-WXYZ");
            var info = noEnv.Resolve();
            Assert.Equal(ApiKeySource.EncryptedFile, info.Source);
            Assert.Equal("WXYZ", info.Last4);
            Assert.DoesNotContain("sk-ant", System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(file)));

            var withEnv = new ApiKeyStore(file, _ => "sk-env-ABCD");
            Assert.Equal(ApiKeySource.EnvironmentVariable, withEnv.Resolve().Source);
            Assert.Equal("ABCD", withEnv.Resolve().Last4);

            Assert.True(noEnv.Clear());
            Assert.False(noEnv.Clear());
            Assert.Equal(ApiKeySource.None, noEnv.Resolve().Source);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }
}
