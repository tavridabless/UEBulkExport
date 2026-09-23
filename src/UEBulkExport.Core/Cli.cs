using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Versions;
using CUE4Parse_Conversion.Options;

namespace UEBulkExport;

public static class Cli
{
    public static string Version =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown";

    /// <summary>Returns null when the run is over before it starts: --help or --version.</summary>
    public static Options? Parse(string[] args)
    {
        if (args.Length == 0 || args.Contains("--help") || args.Contains("-h") || args.Contains("/?"))
        {
            Log.Raw(Options.Usage);
            return null;
        }

        if (args.Contains("--version"))
        {
            Log.Raw($"UEBulkExport {Version}");
            return null;
        }

        var o = ParseArguments(args);
        Prepare(o);
        return o;
    }

    /// <summary>Turns the raw argument list into an <see cref="Options"/> without validating or resolving anything.</summary>
    public static Options ParseArguments(string[] args)
    {
        var o = new Options();

        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            string Next(string name) =>
                i + 1 < args.Length ? args[++i] : throw new UserFacingException($"{name} needs a value.");

            switch (a)
            {
                case "--paks": o.PaksDirectory = Next(a); break;
                case "--out": o.OutputDirectory = Next(a); break;
                case "--usmap": o.UsmapPath = Next(a); break;
                case "--oodle": o.OodlePath = Next(a); break;
                case "--zlib": o.ZlibPath = Next(a); break;
                case "--vgmstream": o.VgmStreamPath = Next(a); break;
                case "--retoc": o.RetocPath = Next(a); break;
                case "--game": o.Game = ParseGame(Next(a)); break;
                case "--platform": o.Platform = ParseEnum<ETexturePlatform>(Next(a), a); break;
                case "--mode": o.Mode = ParseEnum<ExportMode>(Next(a), a); break;
                case "--aes": o.AesKeys.Add(Next(a)); break;
                case "--threads": o.Threads = ParseThreads(Next(a)); break;
                case "--include": o.IncludeRegex = Next(a); break;
                case "--exclude": o.ExcludeRegex = Next(a); break;
                case "--paths-file": o.PathsFile = Next(a); break;
                case "--no-json": o.WriteJson = false; break;
                case "--no-assets": o.WriteAssets = false; break;
                case "--no-raw-misc": o.WriteRawMisc = false; break;
                case "--no-worlds": o.ExportWorlds = false; break;
                case "--no-audio-convert": o.ConvertAudio = false; break;
                case "--raw-packages": o.WriteRawPackages = true; break;
                case "--materials": o.ExportMaterials = true; break;
                case "--overwrite": o.Resume = false; break;
                case "--no-morphs": o.ExportMorphTargets = false; break;
                case "--all-mips": o.ExportAllTextureMips = true; break;
                case "--mesh": o.MeshFormat = ParseMesh(Next(a)); break;
                case "--anim": o.AnimFormatOverride = ParseAnim(Next(a)); break;
                case "--texture": o.TextureFormat = ParseEnum<ETextureFormat>(Next(a), a); break;
                case "--quality": o.MeshQuality = ParseEnum<EMeshQuality>(Next(a), a); break;
                case "--nanite": o.NaniteMeshFormat = ParseEnum<ENaniteMeshFormat>(Next(a), a); break;
                case "--sockets": o.SocketFormat = ParseEnum<ESocketFormat>(Next(a), a); break;
                case "--dry-run": o.DryRun = true; break;
                case "--verbose": o.Verbose = true; break;
                default:
                    throw new UserFacingException($"Unknown argument '{a}'.", "Run UEBulkExport.Cli --help for the full list.");
            }
        }

        return o;
    }

    /// <summary>
    /// Validates the options, resolves what the user left implicit (the Paks folder, a lone
    /// .usmap) and checks the rules that depend on the resolved paths. Both front ends call this
    /// before handing the options to the exporter.
    /// </summary>
    public static void Prepare(Options o)
    {
        Validate(o);
        Resolve(o);
        ValidateResolved(o);
    }

    private static void Validate(Options o)
    {
        if (string.IsNullOrWhiteSpace(o.PaksDirectory))
            throw new UserFacingException("--paks is required.",
                "Point it at the game's Paks folder, at the game's root, or at a single .utoc file.");

        if (string.IsNullOrWhiteSpace(o.OutputDirectory) && o.Mode != ExportMode.List)
            throw new UserFacingException("--out is required.", "It is the folder the export is written into.");

        if (o.UsmapPath is not null && !File.Exists(o.UsmapPath))
            throw new UserFacingException($"Mappings file not found: {o.UsmapPath}");

        if (o.VgmStreamPath is not null && !File.Exists(o.VgmStreamPath))
            throw new UserFacingException($"vgmstream not found: {o.VgmStreamPath}");

        if (o.RetocPath is not null && !File.Exists(o.RetocPath))
            throw new UserFacingException($"retoc not found: {o.RetocPath}");

        if (o.PathsFile is not null && !File.Exists(o.PathsFile))
            throw new UserFacingException($"Paths file not found: {o.PathsFile}");

        foreach (var key in o.AesKeys) ValidateAesKey(key);

        _ = CompileFilter(o.IncludeRegex, "--include");
        _ = CompileFilter(o.ExcludeRegex, "--exclude");
    }

    /// <summary>Checks rules that depend on the actual container directory.</summary>
    private static void ValidateResolved(Options o)
    {
        if (o.Mode != ExportMode.Legacy || !Discovery.HasIoStoreContainers(o.PaksDirectory)) return;

        if (o.IncludeRegex is not null || o.ExcludeRegex is not null || o.SelectedPaths is not null)
            throw new UserFacingException(
                "--include/--exclude/--paths-file cannot be used with legacy IoStore conversion.",
                "retoc cannot apply regular-expression filters. Use --mode raw for filtered byte dumps.");

        // Better to learn this now than after the containers have been mounted and retoc fetched.
        if (o.AesKeys.Count > 1)
            throw new UserFacingException(
                "retoc accepts only one AES key for a conversion run.",
                "Run containers with different keys separately, using one --aes value each time.");
    }

    internal static Regex? CompileFilter(string? pattern, string argument)
    {
        if (pattern is null) return null;

        try { return new Regex(pattern, RegexOptions.IgnoreCase); }
        catch (ArgumentException e)
        {
            throw new UserFacingException($"{argument} is not a valid regular expression: {e.Message}");
        }
    }

    /// <summary>Fills in whatever the user did not have to spell out.</summary>
    private static void Resolve(Options o)
    {
        o.PaksDirectory = Discovery.ResolvePaksDirectory(o.PaksDirectory);

        if (o.UsmapPath is null && o.RequiresMappings)
            o.UsmapPath = Discovery.FindMappings(o.PaksDirectory, o.OutputDirectory);

        if (o.SelectedPaths is null && o.PathsFile is not null)
            o.SelectedPaths = File.ReadLines(o.PathsFile)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.StartsWith('#'))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ AES keys

    private static readonly Regex HexKey = new("^(0x)?[0-9A-Fa-f]{64}$", RegexOptions.Compiled);

    /// <summary>
    /// Splits <c>guid:0xKEY</c> into its parts; a bare key comes back with a null GUID. The GUID
    /// form ties the key to one container, the bare form applies to every container.
    /// </summary>
    public static (string? Guid, string Key) SplitAesKey(string raw)
    {
        raw = raw.Trim();
        var separator = raw.LastIndexOf(':');

        if (separator > 0 && !raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return (raw[..separator].Trim(), raw[(separator + 1)..].Trim());

        return (null, raw);
    }

    /// <summary>A 256-bit AES key is 64 hex characters; anything else fails deep inside CUE4Parse with a stack trace.</summary>
    public static void ValidateAesKey(string raw)
    {
        var (guid, key) = SplitAesKey(raw);

        if (!HexKey.IsMatch(key))
            throw new UserFacingException(
                $"'{raw}' is not a valid AES key.",
                "Expected 64 hexadecimal characters, usually written as 0x0123...CDEF, optionally as <guid>:0x....");

        if (guid is not null && !Guid.TryParse(guid, out _))
            throw new UserFacingException(
                $"'{guid}' is not a valid container GUID.",
                "The form is <guid>:0x<64 hex chars>, for example 00000000-0000-0000-0000-000000000000:0x....");
    }

    // ------------------------------------------------------------------ options -> arguments

    /// <summary>
    /// The inverse of <see cref="ParseArguments"/>: the argument list that reproduces these
    /// options. Defaults are left out so the command stays readable. The GUI shows this so that
    /// anything set up by clicking can be pasted into a script.
    /// </summary>
    public static List<string> ToArguments(Options o)
    {
        var d = new Options();
        var args = new List<string>();

        void Add(string flag, string? value)
        {
            if (string.IsNullOrEmpty(value)) return;
            args.Add(flag);
            args.Add(value);
        }

        void Flag(string flag, bool set)
        {
            if (set) args.Add(flag);
        }

        Add("--paks", o.PaksDirectory);
        Add("--out", o.OutputDirectory);
        if (o.Mode != d.Mode) Add("--mode", o.Mode.ToString().ToLowerInvariant());
        if (o.Game != d.Game) Add("--game", o.Game.ToString());
        Add("--usmap", o.UsmapPath);
        foreach (var key in o.AesKeys) Add("--aes", key);
        if (o.Threads != d.Threads) Add("--threads", o.Threads.ToString());
        Add("--include", o.IncludeRegex);
        Add("--exclude", o.ExcludeRegex);
        Add("--paths-file", o.PathsFile);

        Flag("--no-json", !o.WriteJson);
        Flag("--no-assets", !o.WriteAssets);
        Flag("--no-raw-misc", !o.WriteRawMisc);
        Flag("--no-worlds", !o.ExportWorlds);
        Flag("--no-audio-convert", !o.ConvertAudio);
        Flag("--raw-packages", o.WriteRawPackages);
        Flag("--materials", o.ExportMaterials);
        Flag("--overwrite", !o.Resume);
        Flag("--no-morphs", !o.ExportMorphTargets);
        Flag("--all-mips", o.ExportAllTextureMips);

        if (o.MeshFormat != d.MeshFormat) Add("--mesh", MeshName(o.MeshFormat));
        if (o.AnimFormatOverride is { } anim) Add("--anim", MeshName(anim));
        if (o.TextureFormat != d.TextureFormat) Add("--texture", o.TextureFormat.ToString().ToLowerInvariant());
        if (o.MeshQuality != d.MeshQuality) Add("--quality", o.MeshQuality.ToString().ToLowerInvariant());
        if (o.NaniteMeshFormat != d.NaniteMeshFormat) Add("--nanite", o.NaniteMeshFormat.ToString().ToLowerInvariant());
        if (o.SocketFormat != d.SocketFormat) Add("--sockets", o.SocketFormat.ToString().ToLowerInvariant());
        if (o.Platform != d.Platform) Add("--platform", o.Platform.ToString());

        Add("--oodle", o.OodlePath);
        Add("--zlib", o.ZlibPath);
        Add("--vgmstream", o.VgmStreamPath);
        Add("--retoc", o.RetocPath);

        Flag("--dry-run", o.DryRun);
        Flag("--verbose", o.Verbose);

        return args;
    }

    /// <summary>A single command line, quoted for cmd.exe / PowerShell / POSIX shells alike.</summary>
    public static string FormatCommand(Options o, string executable = "UEBulkExport.Cli")
    {
        var sb = new StringBuilder(Quote(executable));
        foreach (var arg in ToArguments(o)) sb.Append(' ').Append(Quote(arg));
        return sb.ToString();
    }

    private static string Quote(string s) =>
        s.Length > 0 && s.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or ':' or '/' or '\\' or '=')
            ? s
            : "\"" + s.Replace("\"", "\\\"") + "\"";

    private static string MeshName(EMeshFormat f) => f switch
    {
        EMeshFormat.Gltf2 => "gltf2",
        EMeshFormat.ActorX => "actorx",
        EMeshFormat.UEFormat => "ueformat",
        EMeshFormat.USD => "usd",
        _ => f.ToString().ToLowerInvariant()
    };

    // ------------------------------------------------------------------ value parsers

    private static int ParseThreads(string value)
    {
        if (!int.TryParse(value, out var threads) || threads < 1)
            throw new UserFacingException($"--threads expects a positive number, got '{value}'.");

        return threads;
    }

    private static T ParseEnum<T>(string value, string argument) where T : struct, Enum
    {
        if (Enum.TryParse<T>(value, true, out var parsed) && Enum.IsDefined(parsed)) return parsed;

        throw new UserFacingException(
            $"Unknown value '{value}' for {argument}.",
            "Valid values: " + string.Join(", ", Enum.GetNames<T>().Select(n => n.ToLowerInvariant())));
    }

    public static EMeshFormat ParseMesh(string s) => s.ToLowerInvariant() switch
    {
        "gltf" or "gltf2" or "glb" => EMeshFormat.Gltf2,
        "actorx" or "psk" => EMeshFormat.ActorX,
        "ueformat" or "uemodel" => EMeshFormat.UEFormat,
        "usd" or "usda" => EMeshFormat.USD,
        _ => throw new UserFacingException($"Unknown mesh format '{s}'.",
            "Valid values: gltf2, actorx, ueformat, usd")
    };

    public static EMeshFormat ParseAnim(string s)
    {
        var format = ParseMesh(s);
        if (format == EMeshFormat.Gltf2)
            throw new UserFacingException(
                "glTF cannot hold an animation or a skeleton on its own.",
                "Use --anim actorx, ueformat or usd.");

        return format;
    }

    /// <summary>Accepts "5.3", "UE5_3" and "GAME_UE5_3" alike.</summary>
    public static EGame ParseGame(string s)
    {
        if (Enum.TryParse<EGame>(s, true, out var g)) return g;
        if (Enum.TryParse($"GAME_{s}", true, out g)) return g;

        var dotted = s.TrimStart('U', 'E', 'u', 'e').Replace('.', '_');
        if (Enum.TryParse($"GAME_UE{dotted}", true, out g)) return g;

        throw new UserFacingException($"Unknown engine version '{s}'.",
            "Expected something like 5.3, UE5_3 or GAME_UE5_3.");
    }
}
