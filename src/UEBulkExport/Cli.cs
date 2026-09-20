using System.Reflection;
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
            Console.WriteLine(Options.Usage);
            return null;
        }

        if (args.Contains("--version"))
        {
            Console.WriteLine($"UEBulkExport {Version}");
            return null;
        }

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
                    throw new UserFacingException($"Unknown argument '{a}'.", "Run UEBulkExport --help for the full list.");
            }
        }

        Validate(o);
        Resolve(o);
        ValidateResolved(o);
        return o;
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

        _ = CompileFilter(o.IncludeRegex, "--include");
        _ = CompileFilter(o.ExcludeRegex, "--exclude");
    }

    /// <summary>Checks rules that depend on the actual container directory.</summary>
    private static void ValidateResolved(Options o)
    {
        if (o.Mode == ExportMode.Legacy &&
            (o.IncludeRegex is not null || o.ExcludeRegex is not null) &&
            Discovery.HasIoStoreContainers(o.PaksDirectory))
        {
            throw new UserFacingException(
                "--include/--exclude cannot be used with legacy IoStore conversion.",
                "retoc cannot apply regular-expression filters. Use --mode raw for filtered byte dumps.");
        }
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
    }

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

    private static EMeshFormat ParseMesh(string s) => s.ToLowerInvariant() switch
    {
        "gltf" or "gltf2" or "glb" => EMeshFormat.Gltf2,
        "actorx" or "psk" => EMeshFormat.ActorX,
        "ueformat" or "uemodel" => EMeshFormat.UEFormat,
        "usd" or "usda" => EMeshFormat.USD,
        _ => throw new UserFacingException($"Unknown mesh format '{s}'.",
            "Valid values: gltf2, actorx, ueformat, usd")
    };

    private static EMeshFormat ParseAnim(string s)
    {
        var format = ParseMesh(s);
        if (format == EMeshFormat.Gltf2)
            throw new UserFacingException(
                "glTF cannot hold an animation or a skeleton on its own.",
                "Use --anim actorx, ueformat or usd.");

        return format;
    }

    /// <summary>Accepts "5.3", "UE5_3" and "GAME_UE5_3" alike.</summary>
    private static EGame ParseGame(string s)
    {
        if (Enum.TryParse<EGame>(s, true, out var g)) return g;
        if (Enum.TryParse($"GAME_{s}", true, out g)) return g;

        var dotted = s.TrimStart('U', 'E', 'u', 'e').Replace('.', '_');
        if (Enum.TryParse($"GAME_UE{dotted}", true, out g)) return g;

        throw new UserFacingException($"Unknown engine version '{s}'.",
            "Expected something like 5.3, UE5_3 or GAME_UE5_3.");
    }
}
