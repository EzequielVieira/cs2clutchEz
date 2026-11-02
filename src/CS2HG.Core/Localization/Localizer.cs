using System.ComponentModel;
using System.Globalization;
using System.Text.Json;

namespace CS2HG.Core.Localization;

public sealed record LanguageInfo(string Code, string NativeName)
{
    public override string ToString() => NativeName;
}

/// <summary>
/// UI and message strings for every supported language, loaded from embedded JSON files
/// (Localization/&lt;code&gt;.json). Missing keys fall back to English, then to the key itself.
/// Binding to the indexer (<c>[key]</c>) updates live when the language changes.
/// </summary>
public sealed class Localizer : INotifyPropertyChanged
{
    public const string Fallback = "en";

    public static readonly IReadOnlyList<LanguageInfo> Languages =
    [
        new("pt-BR", "Português (Brasil)"),
        new("en", "English"),
        new("es", "Español"),
    ];

    public static Localizer Instance { get; } = new();

    private readonly Dictionary<string, Dictionary<string, string>> _tables = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _current;
    private readonly Dictionary<string, string> _fallback;

    private Localizer()
    {
        foreach (var lang in Languages) _tables[lang.Code] = Load(lang.Code);
        _fallback = _tables[Fallback];
        Language = ResolveSystemLanguage();
        _current = _tables[Language];
        Culture = CultureInfo.GetCultureInfo(Language);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event Action? LanguageChanged;

    public string Language { get; private set; }

    public CultureInfo Culture { get; private set; }

    public string this[string key] =>
        _current.TryGetValue(key, out var v) ? v : _fallback.TryGetValue(key, out var f) ? f : key;

    public IReadOnlyCollection<string> Keys(string language) => _tables[language].Keys;

    public bool Has(string language, string key) => _tables.TryGetValue(language, out var t) && t.ContainsKey(key);

    /// <summary>Sets the language; null or unknown codes use the Windows display language.</summary>
    public void SetLanguage(string? code)
    {
        var resolved = Normalize(code) ?? ResolveSystemLanguage();
        if (resolved == Language) return;
        Language = resolved;
        _current = _tables[resolved];
        Culture = CultureInfo.GetCultureInfo(resolved);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
        LanguageChanged?.Invoke();
    }

    public static string ResolveSystemLanguage() => Normalize(CultureInfo.CurrentUICulture.Name) ?? Fallback;

    public static string? Normalize(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        if (code.StartsWith("pt", StringComparison.OrdinalIgnoreCase)) return "pt-BR";
        if (code.StartsWith("es", StringComparison.OrdinalIgnoreCase)) return "es";
        if (code.StartsWith("en", StringComparison.OrdinalIgnoreCase)) return "en";
        return null;
    }

    private static Dictionary<string, string> Load(string code)
    {
        var asm = typeof(Localizer).Assembly;
        using var stream = asm.GetManifestResourceStream($"CS2HG.Core.Localization.{code}.json");
        if (stream is null) return new Dictionary<string, string>();
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? new Dictionary<string, string>();
    }
}

/// <summary>Short helpers: <c>L.T(key)</c> and <c>L.F(key, args)</c>.</summary>
public static class L
{
    public static string T(string key) => Localizer.Instance[key];

    public static string F(string key, params object?[] args) => string.Format(Localizer.Instance.Culture, Localizer.Instance[key], args);
}
