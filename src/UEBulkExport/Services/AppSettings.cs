using System.Text.Json;
using System.Text.Json.Serialization;

namespace UEBulkExport.Gui.Services;

/// <summary>
/// Per-user preferences and the list of recent games. Stored as JSON under LocalAppData; nothing
/// in here ever leaves the machine. Missing or corrupt files fall back to defaults silently.
/// </summary>
public sealed class AppSettings
{
    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "UEBulkExport", "settings.json");

    public string Language { get; set; } = "";
    public string Theme { get; set; } = "Light";
    public bool RememberPaths { get; set; } = true;
    public bool ConfirmCloseWhileRunning { get; set; } = true;
    public bool TransparencyEnabled { get; set; } = true;
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
    public string UModelPath { get; set; } = "";
    public string TargetProjectPath { get; set; } = "";
    public string UnrealEditorPath { get; set; } = "";
    public string ConversionDestinationPath { get; set; } = "/Game/ConvertedDump";

    public double WindowWidth { get; set; } = 1240;
    public double WindowHeight { get; set; } = 820;

    public List<RecentGame> Recent { get; set; } = [];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault
    };

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
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new AppSettings();
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
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"could not save settings: {e.Message}");
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
