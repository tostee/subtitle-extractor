using System.Text;
using SubtitleTool.App.Models;
using SubtitleTool.App.Services;
using SubtitleTool.App.ViewModels;
using SubtitleTool.Core.Translation;

namespace SubtitleTool.App.Tests;

public sealed class SettingsAndGlossaryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "subtool-app-tests-" + Guid.NewGuid().ToString("N"));

    public SettingsAndGlossaryTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static SettingsViewModel Settings(Harness h, AppSettings? s = null) =>
        new(s ?? new AppSettings(), h.Library, h.Doctor, h.ApiKeys, h.Dialogs);

    // ------------------------------------------------------------------ ajustes

    [Fact]
    public void Defaults_MatchSpecification()
    {
        var s = new AppSettings();
        Assert.Equal(TranslationBackends.ClaudeCode, s.Backend);
        Assert.Equal("claude-opus-5-5", s.Model);
        Assert.Equal("medium", s.Effort);
        Assert.Equal(150, s.ChunkSize);
        Assert.Equal(10, s.TimeoutMinutes);
        Assert.True(s.CreatePlainCopy);
        Assert.Null(s.MkvToolNixDirectory);
    }

    [Fact]
    public void JsonSettingsStore_RoundTrips_AndNeverContainsKey()
    {
        var file = Path.Combine(_dir, "settings.json");
        var store = new JsonSettingsStore(file);
        store.Save(new AppSettings { Backend = TranslationBackends.Api, Model = "claude-haiku-5-5", ChunkSize = 99, MkvToolNixDirectory = @"D:\mkv" });

        var loaded = store.Load();
        Assert.Equal(TranslationBackends.Api, loaded.Backend);
        Assert.Equal("claude-haiku-5-5", loaded.Model);
        Assert.Equal(99, loaded.ChunkSize);
        Assert.Equal(@"D:\mkv", loaded.MkvToolNixDirectory);
        var json = File.ReadAllText(file);
        Assert.DoesNotContain("key", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void JsonSettingsStore_CorruptOrUnknownValues_FallBackToDefaults()
    {
        var file = Path.Combine(_dir, "settings.json");
        File.WriteAllText(file, "{ esto no es json");
        Assert.Equal(150, new JsonSettingsStore(file).Load().ChunkSize);

        File.WriteAllText(file, """{"backend":"otro","model":"gpt","effort":"max","chunkSize":0}""");
        var s = new JsonSettingsStore(file).Load();
        Assert.Equal(TranslationBackends.ClaudeCode, s.Backend);
        Assert.Equal("claude-opus-5-5", s.Model);
        Assert.Equal("medium", s.Effort);
        Assert.Equal(150, s.ChunkSize);
    }

    [Fact]
    public void TrySave_ValidatesNumbersAndFolder()
    {
        var vm = Settings(new Harness());
        vm.ChunkSizeText = "0";
        Assert.False(vm.TrySave());
        Assert.Contains("tramo", vm.ErrorText);

        vm.ChunkSizeText = "150";
        vm.TimeoutText = "abc";
        Assert.False(vm.TrySave());

        vm.TimeoutText = "15";
        vm.MkvToolNixDirectory = Path.Combine(_dir, "no-existe");
        Assert.False(vm.TrySave());
        Assert.Contains("no existe", vm.ErrorText);

        vm.MkvToolNixDirectory = _dir;
        vm.CreatePlainCopy = false;
        Assert.True(vm.TrySave());
        Assert.Equal(15, vm.Result.TimeoutMinutes);
        Assert.Equal(_dir, vm.Result.MkvToolNixDirectory);
        Assert.False(vm.Result.CreatePlainCopy);
        Assert.Equal("", vm.ErrorText);
    }

    [Fact]
    public void TrySave_ValidatesFfmpegFolder_AndSettingsRoundTrip()
    {
        var vm = Settings(new Harness());
        vm.FfmpegDirectory = Path.Combine(_dir, "no-existe");
        Assert.False(vm.TrySave());
        Assert.Contains("FFmpeg", vm.ErrorText);

        vm.FfmpegDirectory = "\"" + _dir + "\"";
        Assert.True(vm.TrySave());
        Assert.Equal(_dir, vm.Result.FfmpegDirectory);
        Assert.Equal(_dir, vm.Result.Tools.Ffmpeg);
        Assert.StartsWith("Autodetectado:", vm.DetectedFfmpegText);

        var store = new JsonSettingsStore(Path.Combine(_dir, "s.json"));
        store.Save(vm.Result);
        Assert.Equal(_dir, store.Load().FfmpegDirectory);
        Assert.Null(new AppSettings { FfmpegDirectory = "  " }.Normalize().FfmpegDirectory);
    }

    [Fact]
    public void ApiKey_SaveShowsOnlyLast4_AndClear()
    {
        var h = new Harness();
        var vm = Settings(h);
        Assert.Contains("No configurada", vm.ApiKeyStatus);
        Assert.False(vm.CanClearApiKey);

        vm.NewApiKey = "sk-ant-secret-ABCD";
        Assert.True(vm.SaveApiKey());
        Assert.Equal("sk-ant-secret-ABCD", h.ApiKeys.Key);
        Assert.Equal("Guardada cifrada (…ABCD).", vm.ApiKeyStatus);
        Assert.DoesNotContain("secret", vm.ApiKeyStatus);
        Assert.Equal("", vm.NewApiKey);
        Assert.True(vm.CanClearApiKey);

        vm.ClearApiKeyCommand.Execute(null);
        Assert.Null(h.ApiKeys.Key);
        Assert.False(vm.CanClearApiKey);
    }

    [Fact]
    public void ApiKey_EmptyIsRejected()
    {
        var h = new Harness();
        var vm = Settings(h);
        vm.NewApiKey = "  ";
        Assert.False(vm.SaveApiKey());
        Assert.Null(h.ApiKeys.Key);
    }

    [Fact]
    public async Task Check_RunsDoctor_WithEditedMkvFolder()
    {
        var h = new Harness();
        var vm = Settings(h);
        vm.MkvToolNixDirectory = @"E:\MKV";

        await vm.CheckCommand.ExecuteAsync(null);

        Assert.Equal(@"E:\MKV", h.Doctor.LastTools?.MkvToolNix);
        Assert.Null(h.Doctor.LastTools?.Ffmpeg);
        Assert.Equal(3, vm.Checks.Count);
        Assert.Equal("✅ Todo listo.", vm.CheckStatus);
        Assert.False(vm.IsChecking);
        Assert.StartsWith("Autodetectado:", vm.DetectedMkvToolNixText);
    }

    // ------------------------------------------------------------------ glosario

    private const string GlossaryPath = @"X:\Pelis\a.glossary.json";
    private const string EnglishPath = @"X:\Pelis\a.en.srt";

    private static GlossaryEditorViewModel Editor(Harness h, string glossaryPath = GlossaryPath, string englishPath = EnglishPath) =>
        new(glossaryPath, englishPath, h.Glossary, h.Dialogs, () => new AppSettings());

    [Fact]
    public void Glossary_LoadsBothTables()
    {
        var h = new Harness();
        h.Glossary.Files[GlossaryPath] = new Glossary
        {
            Terms = [new GlossaryTerm { En = "Odysseus", Es = "Odiseo", Note = "rey" }],
            Address = [new AddressRule { A = "Odysseus", B = "Athena", Form = "usted", Note = "diosa" }],
        };
        var vm = Editor(h);

        Assert.True(vm.Load());

        Assert.Equal("Odiseo", Assert.Single(vm.Terms).Es);
        Assert.Equal("usted", Assert.Single(vm.Address).Form);
        Assert.Contains("1 término(s), 1 tratamiento(s)", vm.StatusText);
    }

    [Fact]
    public void Glossary_MissingFile_StartsEmpty()
    {
        var vm = Editor(new Harness());
        Assert.True(vm.Load());
        Assert.Empty(vm.Terms);
        Assert.Contains("Aún no existe", vm.StatusText);
    }

    [Fact]
    public void Glossary_InvalidFile_ShowsError_AndCanBeReplaced()
    {
        var h = new Harness();
        h.Glossary.Invalid.Add(GlossaryPath);
        var vm = Editor(h);
        Assert.True(vm.Load());
        Assert.Single(h.Dialogs.Errors);
        Assert.Contains("no es válido", vm.StatusText);
    }

    [Fact]
    public void Glossary_Save_ValidatesBeforeWriting()
    {
        var h = new Harness();
        var vm = Editor(h);
        vm.Load();
        vm.Terms.Add(new TermRow { En = "Ithaca", Es = "" });
        vm.Address.Add(new AddressRow { A = "Penelope", B = "Telemachus", Form = "vos" });

        vm.SaveCommand.Execute(null);

        Assert.False(h.Glossary.Files.ContainsKey(GlossaryPath));
        Assert.StartsWith("No se guardó:", vm.StatusText);
        Assert.Contains("Término 1", vm.StatusText);
        Assert.Contains("«tú» o «usted»", vm.StatusText);
    }

    [Fact]
    public void Glossary_Save_IgnoresEmptyRows_AndTrims()
    {
        var h = new Harness();
        var vm = Editor(h);
        vm.Load();
        vm.Terms.Add(new TermRow { En = " Ithaca ", Es = "Ítaca", Note = "isla" });
        vm.Terms.Add(new TermRow());
        vm.Address.Add(new AddressRow { A = "Penelope", B = "Telemachus", Form = "tú" });

        vm.SaveCommand.Execute(null);

        var g = h.Glossary.Files[GlossaryPath];
        Assert.Equal("Ithaca", Assert.Single(g.Terms).En);
        Assert.Equal("tú", Assert.Single(g.Address).Form);
        Assert.StartsWith("Guardado", vm.StatusText);
    }

    [Fact]
    public void Glossary_DuplicateTerms_AreRejected()
    {
        var h = new Harness();
        var vm = Editor(h);
        vm.Load();
        vm.Terms.Add(new TermRow { En = "Troy", Es = "Troya" });
        vm.Terms.Add(new TermRow { En = "troy", Es = "Troya" });
        var (g, errors) = vm.BuildGlossary();
        Assert.Null(g);
        Assert.Contains(errors, e => e.Contains("repetidos"));
    }

    [Fact]
    public async Task Glossary_Regenerate_UsesServiceAfterConfirmation()
    {
        var h = new Harness();
        var english = Path.Combine(_dir, "a.en.srt");
        File.WriteAllText(english, "1\r\n00:00:01,000 --> 00:00:02,000\r\nHi\r\n\r\n");
        var glossary = Path.Combine(_dir, "a.glossary.json");
        var vm = Editor(h, glossary, english);
        vm.Load();

        h.Dialogs.ConfirmResult = false;
        await vm.RegenerateCommand.ExecuteAsync(null);
        Assert.Equal(0, h.Glossary.RegenerateCount);

        h.Dialogs.ConfirmResult = true;
        await vm.RegenerateCommand.ExecuteAsync(null);
        Assert.Equal(1, h.Glossary.RegenerateCount);
        Assert.Equal("Ítaca", Assert.Single(vm.Terms).Es);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Glossary_Regenerate_RequiresEnglishFile()
    {
        var h = new Harness();
        var vm = Editor(h, Path.Combine(_dir, "b.glossary.json"), Path.Combine(_dir, "b.en.srt"));
        vm.Load();
        await vm.RegenerateCommand.ExecuteAsync(null);
        Assert.Equal(0, h.Glossary.RegenerateCount);
        Assert.Contains("extraer", vm.StatusText);
    }

    [Fact]
    public void GlossaryService_RoundTrip_ProducesJsonReadableByTranslator()
    {
        var path = Path.Combine(_dir, "x.glossary.json");
        var service = new GlossaryService();
        Assert.Null(service.Load(path));

        service.Save(path, new Glossary
        {
            Terms = [new GlossaryTerm { En = "Cyclops", Es = "Cíclope" }],
            Address = [new AddressRule { A = "A", B = "B", Form = "usted" }],
        });

        var parsed = TranslationPrompts.ParseGlossary(File.ReadAllText(path, Encoding.UTF8));
        Assert.NotNull(parsed);
        Assert.Equal("Cíclope", Assert.Single(parsed!.Terms).Es);
        Assert.Equal("usted", service.Load(path)!.Address[0].Form);

        File.WriteAllText(path, "{roto");
        Assert.Throws<InvalidDataException>(() => service.Load(path));
    }
}
