using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using CS2HG.Core.Localization;
using CS2HG.Core.Models;
using CS2HG.HighlightDetection;

namespace CS2HG.Tests;

internal static class TestLanguage
{
    /// <summary>Detector assertions check the Portuguese texts, so every run uses pt-BR whatever the machine's language.</summary>
    [ModuleInitializer]
    internal static void Init() => Localizer.Instance.SetLanguage("pt-BR");
}

[CollectionDefinition(nameof(LanguageSwitching), DisableParallelization = true)]
public sealed class LanguageSwitching;

public partial class LocalizationTests
{
    private static readonly string[] Codes = Localizer.Languages.Select(l => l.Code).ToArray();

    [Fact]
    public void All_languages_define_the_same_keys()
    {
        var en = Localizer.Instance.Keys("en").ToHashSet();
        Assert.True(en.Count > 300);
        foreach (var code in Codes)
        {
            var keys = Localizer.Instance.Keys(code).ToHashSet();
            Assert.Empty(en.Except(keys));
            Assert.Empty(keys.Except(en));
        }
    }

    [Fact]
    public void Translations_use_the_same_placeholders()
    {
        foreach (var key in Localizer.Instance.Keys("en"))
        {
            var expected = Placeholders(Value("en", key));
            foreach (var code in Codes)
                Assert.True(expected.SetEquals(Placeholders(Value(code, key))), $"{code}: placeholders of '{key}' differ from English");
        }
    }

    [Fact]
    public void Every_key_used_in_the_source_exists()
    {
        var src = Path.Combine(RepoRoot(), "src");
        var used = new HashSet<string>();
        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories).Where(NotGenerated))
            foreach (Match m in CodeKey().Matches(File.ReadAllText(file)))
                used.Add(m.Groups[1].Value);
        foreach (var file in Directory.EnumerateFiles(src, "*.xaml", SearchOption.AllDirectories).Where(NotGenerated))
            foreach (Match m in XamlKey().Matches(File.ReadAllText(file)))
                used.Add(m.Groups[1].Value);

        // Keys built at run time from a prefix.
        used.RemoveWhere(k => k.EndsWith('.'));
        used.UnionWith(Enum.GetNames<HighlightKind>().Select(k => "kind." + k));
        used.UnionWith(Enum.GetNames<HighlightCategory>().Select(c => "category." + c));
        used.UnionWith(Enum.GetNames<RenderJobStatus>().Select(s => "jobStatus." + s));
        used.UnionWith(["warn.bots", "warn.noRounds", "category.All"]);

        Assert.True(used.Count > 300);
        var missing = used.Where(k => !Localizer.Instance.Has("en", k)).Order().ToList();
        Assert.Empty(missing);
    }

    [Fact]
    public void Every_detector_has_a_translated_name_and_description()
    {
        foreach (var detector in HighlightAnalyzer.DefaultDetectors())
        {
            Assert.DoesNotContain("det.", detector.Name);
            Assert.DoesNotContain("det.", detector.Description);
        }
    }

    [Fact]
    public void Language_codes_are_normalized()
    {
        Assert.Equal("pt-BR", Localizer.Normalize("pt-PT"));
        Assert.Equal("es", Localizer.Normalize("es-AR"));
        Assert.Equal("en", Localizer.Normalize("en-GB"));
        Assert.Null(Localizer.Normalize("de-DE"));
        Assert.Null(Localizer.Normalize(""));
    }

    private static string Value(string code, string key)
    {
        var current = Localizer.Instance.Language;
        Assert.True(Localizer.Instance.Has(code, key), $"{code} has no '{key}'");
        return code == current ? Localizer.Instance[key] : Raw(code, key);
    }

    private static readonly Dictionary<string, Dictionary<string, string>> RawTables = new();

    private static string Raw(string code, string key)
    {
        lock (RawTables)
        {
            if (!RawTables.TryGetValue(code, out var table))
            {
                using var s = typeof(Localizer).Assembly.GetManifestResourceStream($"CS2HG.Core.Localization.{code}.json")!;
                table = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(s)!;
                RawTables[code] = table;
            }

            return table[key];
        }
    }

    private static HashSet<string> Placeholders(string text) =>
        Placeholder().Matches(text).Select(m => m.Groups[1].Value).ToHashSet();

    private static bool NotGenerated(string path) =>
        !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
        !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}");

    internal static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CS2DemoHighlightGenerator.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    [GeneratedRegex(@"\bL\.[TF]\(\s*""([A-Za-z0-9_.]+)""")]
    private static partial Regex CodeKey();

    [GeneratedRegex(@"\{l:Tr\s+([A-Za-z0-9_.]+)\s*\}")]
    private static partial Regex XamlKey();

    [GeneratedRegex(@"(?<!\{)\{(\d+)(?:[,:][^}]*)?\}")]
    private static partial Regex Placeholder();
}

[Collection(nameof(LanguageSwitching))]
public class LanguageSwitchTests
{
    [Fact]
    public void Switching_language_changes_texts_and_notifies_bindings()
    {
        var changed = new List<string?>();
        void OnChanged(object? s, System.ComponentModel.PropertyChangedEventArgs e) => changed.Add(e.PropertyName);
        Localizer.Instance.PropertyChanged += OnChanged;
        try
        {
            Localizer.Instance.SetLanguage("en");
            Assert.Equal("Clutch defuse", HighlightKind.DecisiveDefuse.DisplayName());
            Assert.Equal("Knife", WeaponInfo.DisplayName("knife_karambit"));
            Assert.Equal("3 kills in 1.5s", L.F("comp.killsIn", 3, 1.5));

            Localizer.Instance.SetLanguage("es");
            Assert.Equal("Cuchillo", WeaponInfo.DisplayName("knife_karambit"));
            Assert.Equal("3 kills en 1,5s", L.F("comp.killsIn", 3, 1.5));
            Assert.Contains("Item[]", changed);
        }
        finally
        {
            Localizer.Instance.PropertyChanged -= OnChanged;
            Localizer.Instance.SetLanguage("pt-BR");
        }

        Assert.Equal("Defuse decisivo", HighlightKind.DecisiveDefuse.DisplayName());
    }
}
