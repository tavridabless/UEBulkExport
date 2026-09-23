using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace UEBulkExport.Gui.Localization;

/// <summary>
/// String table with live switching. Every visible text goes through <c>Loc.Instance[key]</c>,
/// either directly or through the <c>{loc:Tr Key}</c> markup extension; changing
/// <see cref="Language"/> raises a change for every binding at once, so the window re-labels
/// itself without a restart. Tables live in Strings.&lt;lang&gt;.json as embedded resources.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    // Declared before Instance: static initialisers run in textual order, and the constructor reads it.
    public static readonly IReadOnlyList<LanguageInfo> Languages =
    [
        new("en", "English"),
        new("ru", "Русский")
    ];

    public static Loc Instance { get; } = new();

    private readonly Dictionary<string, Dictionary<string, string>> _tables = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _current;
    private Dictionary<string, string> _fallback;
    private string _language = "en";

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? LanguageChanged;

    private Loc()
    {
        foreach (var info in Languages) _tables[info.Code] = LoadTable(info.Code);
        _fallback = _tables["en"];
        _current = _fallback;
    }

    public string Language
    {
        get => _language;
        set
        {
            var code = _tables.ContainsKey(value) ? value : "en";
            if (code == _language) return;

            _language = code;
            _current = _tables[code];
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(code);

            // An empty name means "everything changed", which is what the indexer bindings need.
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
            LanguageChanged?.Invoke();
        }
    }

    /// <summary>Missing keys show up as the key in brackets rather than as an exception in the UI.</summary>
    public string this[string key] =>
        _current.TryGetValue(key, out var s) || _fallback.TryGetValue(key, out s) ? s : $"[{key}]";

    public string Format(string key, params object[] args)
    {
        var template = this[key];
        try { return string.Format(CultureInfo.CurrentUICulture, template, args); }
        catch (FormatException) { return template; }
    }

    /// <summary>What a fresh install starts with, and what "reset" goes back to.</summary>
    public const string DefaultLanguage = "en";

    private static Dictionary<string, string> LoadTable(string code)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith($"Strings.{code}.json", StringComparison.OrdinalIgnoreCase));

        if (name is null) return new Dictionary<string, string>(StringComparer.Ordinal);

        using var stream = assembly.GetManifestResourceStream(name)!;
        var table = JsonSerializer.Deserialize<Dictionary<string, string>>(stream, new JsonSerializerOptions
        {
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        });

        return table ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }
}

public sealed record LanguageInfo(string Code, string NativeName)
{
    public override string ToString() => NativeName;
}
