using System.Text.Json;

namespace UEBulkExport.Gui.Services;

/// <summary>
/// Per-user preferences and the list of recent games. Stored as JSON under LocalAppData; nothing
/// in here ever leaves the machine. Missing or corrupt files fall back to defaults silently.
/// </summary>
public sealed class AppSettings
{
    public static string DataDirectory
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable("UEBULKEXPORT_DATA_DIR");
            if (!string.IsNullOrWhiteSpace(overridden))
            {
                try { return Path.GetFullPath(overridden); }
                catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { }
            }

            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UEBulkExport");
        }
    }

    public static string FilePath => Path.Combine(DataDirectory, "settings.json");

    public string Language { get; set; } = "";
    public string Theme { get; set; } = "Light";
    public bool RememberPaths { get; set; } = true;
    public bool ConfirmCloseWhileRunning { get; set; } = true;
    public bool TransparencyEnabled { get; set; } = true;
    public bool NotificationsEnabled { get; set; } = true;
    public int DefaultThreads { get; set; } = Math.Max(1, Environment.ProcessorCount - 1);

    public string LastPaksPath { get; set; } = "";
    public string LastOutputPath { get; set; } = "";
    public string LastGame { get; set; } = "";

    public string RetocPath { get; set; } = "";
    public string OodlePath { get; set; } = "";
    public string ZlibPath { get; set; } = "";
    public string VgmStreamPath { get; set; } = "";

    public string ConversionSourcePath { get; set; } = "";
    public string ConversionSourceVersion { get; set; } = "4.27";
    public string ConversionUsmapPath { get; set; } = "";
    public string UModelPath { get; set; } = "";
    public string TargetProjectPath { get; set; } = "";
    public string UnrealEditorPath { get; set; } = "";
    public string ConversionDestinationPath { get; set; } = "/Game/ConvertedDump";

    public double WindowWidth { get; set; } = 1240;
    public double WindowHeight { get; set; } = 820;

    public List<RecentGame> Recent { get; set; } = [];
    public List<string> EnabledPlugins { get; set; } = [];

    // Every property is written, defaults included. Skipping CLR defaults would drop a switch the
    // user turned off (false is the default for bool), and the initializer would then turn it back
    // on at the next start.
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// The language chosen in the installer wizard, written next to the executable as
    /// installer.json. It only applies until the user picks a language in Settings.
    /// </summary>
    public static string? InstallerLanguage()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "installer.json");
            if (!File.Exists(path)) return null;

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("Language", out var language) &&
                   language.ValueKind == JsonValueKind.String &&
                   language.GetString() is { Length: > 0 } code
                ? code
                : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath)) return Deserialize(File.ReadAllText(FilePath));
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // A broken settings file must never keep the window from opening.
        }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, Serialize(this));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"could not save settings: {e.Message}");
        }
    }

    internal static string Serialize(AppSettings settings) => JsonSerializer.Serialize(settings, JsonOptions);

    internal static AppSettings Deserialize(string json)
    {
        var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();

        // An explicit null in the file (a hand edit, or an older version) replaces the initializer,
        // and the pages call Trim() on these paths directly.
        ReplaceNullStrings(loaded);
        loaded.Recent ??= [];
        loaded.EnabledPlugins ??= [];
        loaded.Recent.RemoveAll(game => game is null);
        foreach (var game in loaded.Recent)
        {
            ReplaceNullStrings(game);
            game.AesKeys ??= [];
        }

        return loaded;
    }

    private static void ReplaceNullStrings(object target)
    {
        foreach (var property in target.GetType().GetProperties())
        {
            if (property.PropertyType == typeof(string) && property.CanWrite && property.GetValue(target) is null)
                property.SetValue(target, "");
        }
    }

    /// <summary>Puts the game at the top of the recent list, replacing an older entry for the same folder.</summary>
    public void Remember(RecentGame game)
    {
        Recent.RemoveAll(r => string.Equals(r.PaksPath, game.PaksPath, StringComparison.OrdinalIgnoreCase));
        Recent.Insert(0, game);
        if (Recent.Count > 12) Recent.RemoveRange(12, Recent.Count - 12);
    }
}

public sealed class RecentGame
{
    public string Name { get; set; } = "";
    public string PaksPath { get; set; } = "";
    public string OutputPath { get; set; } = "";
    public string Game { get; set; } = "";
    public string Mode { get; set; } = "";
    public List<string> AesKeys { get; set; } = [];
    public string UsmapPath { get; set; } = "";
    public DateTime LastUsed { get; set; } = DateTime.Now;
}
