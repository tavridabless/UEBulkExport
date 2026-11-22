using System.Text.Json;
using System.Text.Json.Serialization;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Versions;
using CUE4Parse_Conversion.Options;

namespace UEBulkExport;

/// <summary>Versioned export settings that can be reused with a separately chosen local source and output.</summary>
public sealed class ExportRecipe
{
    public const int CurrentVersion = 1;
    private const int MaximumFileSize = 32 * 1024 * 1024;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false) }
    };

    [JsonRequired] public int Version { get; init; } = CurrentVersion;
    public ExportMode Mode { get; init; } = ExportMode.Legacy;
    public EGame Game { get; init; } = EGame.GAME_UE5_3;
    public ETexturePlatform Platform { get; init; } = ETexturePlatform.DesktopMobile;
    public int Threads { get; init; } = 1;
    public long MaxInFlightBytes { get; init; } = 256L * 1024 * 1024;
    public string? IncludeRegex { get; init; }
    public string? ExcludeRegex { get; init; }
    public string[]? SelectedPaths { get; init; }
    public bool WriteJson { get; init; } = true;
    public bool WriteAssets { get; init; } = true;
    public bool WriteRawMisc { get; init; } = true;
    public bool WriteRawPackages { get; init; }
    public bool ExportMaterials { get; init; }
    public bool ConvertAudio { get; init; } = true;
    public bool ExportWorlds { get; init; } = true;
    public bool Resume { get; init; } = true;
    public bool Verbose { get; init; }
    public EMeshFormat MeshFormat { get; init; } = EMeshFormat.Gltf2;
    public EMeshFormat? AnimFormatOverride { get; init; }
    public ENaniteMeshFormat NaniteMeshFormat { get; init; } = ENaniteMeshFormat.NoNanite;
    public EMeshQuality MeshQuality { get; init; } = EMeshQuality.Highest;
    public ETextureFormat TextureFormat { get; init; } = ETextureFormat.Png;
    public ESocketFormat SocketFormat { get; init; } = ESocketFormat.Bone;
    public bool ExportMorphTargets { get; init; } = true;
    public bool ExportAllTextureMips { get; init; }

    public static ExportRecipe Capture(Options options)
    {
        // Serialize an explicit schema. Adding an input or a secret to Options cannot add it here.
        var recipe = new ExportRecipe
        {
            Mode = options.Mode,
            Game = options.Game,
            Platform = options.Platform,
            Threads = options.Threads,
            MaxInFlightBytes = options.MaxInFlightBytes,
            IncludeRegex = options.IncludeRegex,
            ExcludeRegex = options.ExcludeRegex,
            SelectedPaths = NormalizeSelection(options.SelectedPaths),
            WriteJson = options.WriteJson,
            WriteAssets = options.WriteAssets,
            WriteRawMisc = options.WriteRawMisc,
            WriteRawPackages = options.WriteRawPackages,
            ExportMaterials = options.ExportMaterials,
            ConvertAudio = options.ConvertAudio,
            ExportWorlds = options.ExportWorlds,
            Resume = options.Resume,
            Verbose = options.Verbose,
            MeshFormat = options.MeshFormat,
            AnimFormatOverride = options.AnimFormatOverride,
            NaniteMeshFormat = options.NaniteMeshFormat,
            MeshQuality = options.MeshQuality,
            TextureFormat = options.TextureFormat,
            SocketFormat = options.SocketFormat,
            ExportMorphTargets = options.ExportMorphTargets,
            ExportAllTextureMips = options.ExportAllTextureMips
        };

        var selection = recipe.SelectedPaths ?? [];
        foreach (var input in options.AesKeys)
        {
            var (_, key) = Cli.SplitAesKey(input);
            var hex = key.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? key[2..] : key;
            if (hex.Length == 64 && hex.All(Uri.IsHexDigit) &&
                (ContainsKey(recipe.IncludeRegex, hex) || ContainsKey(recipe.ExcludeRegex, hex) ||
                 selection.Any(path => ContainsKey(path, hex))))
                throw new UserFacingException("An AES key also appears in the recipe's filters or selection.",
                    "Remove it from those options and keep it in the separate AES input.");
        }

        recipe.Validate();
        return recipe;
    }

    public static ExportRecipe Load(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            CheckSize(stream.Length);
            var recipe = JsonSerializer.Deserialize<ExportRecipe>(stream, Json) ?? throw InvalidFormat();
            recipe.Validate();
            _ = NormalizeSelection(recipe.SelectedPaths);
            return recipe;
        }
        catch (JsonException) { throw InvalidFormat(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new UserFacingException("The export recipe could not be read.",
                "Choose an existing JSON recipe that this user can read.");
        }
    }

    public void Save(string path)
    {
        Validate();
        _ = NormalizeSelection(SelectedPaths);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(this, Json);
        CheckSize(bytes.Length);
        try { AtomicFile.WriteAllBytes(Path.GetFullPath(path), bytes); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new UserFacingException("The export recipe could not be saved.",
                "Choose a file in a folder that this user can write to.");
        }
    }

    public Options CreateOptions()
    {
        Validate();
        return new Options
        {
            Mode = Mode,
            Game = Game,
            Platform = Platform,
            Threads = Threads,
            MaxInFlightBytes = MaxInFlightBytes,
            IncludeRegex = IncludeRegex,
            ExcludeRegex = ExcludeRegex,
            SelectedPaths = NormalizeSelection(SelectedPaths)?.ToHashSet(StringComparer.OrdinalIgnoreCase),
            WriteJson = WriteJson,
            WriteAssets = WriteAssets,
            WriteRawMisc = WriteRawMisc,
            WriteRawPackages = WriteRawPackages,
            ExportMaterials = ExportMaterials,
            ConvertAudio = ConvertAudio,
            ExportWorlds = ExportWorlds,
            Resume = Resume,
            Verbose = Verbose,
            MeshFormat = MeshFormat,
            AnimFormatOverride = AnimFormatOverride,
            NaniteMeshFormat = NaniteMeshFormat,
            MeshQuality = MeshQuality,
            TextureFormat = TextureFormat,
            SocketFormat = SocketFormat,
            ExportMorphTargets = ExportMorphTargets,
            ExportAllTextureMips = ExportAllTextureMips
        };
    }

    private void Validate()
    {
        if (Version != CurrentVersion)
            throw new UserFacingException($"Export recipe version {Version} is unsupported.",
                $"This version of UEBulkExport reads recipe version {CurrentVersion}.");
        if (Threads < 1 || MaxInFlightBytes < 0)
            throw new UserFacingException("The export recipe has invalid worker or byte-budget settings.");
        if (!Enum.IsDefined(Mode) || !Enum.IsDefined(Game) || !Enum.IsDefined(Platform) ||
            !Enum.IsDefined(TextureFormat) || !Enum.IsDefined(NaniteMeshFormat) ||
            !Enum.IsDefined(MeshQuality) || !Enum.IsDefined(SocketFormat) ||
            MeshFormat is not (EMeshFormat.Gltf2 or EMeshFormat.ActorX or EMeshFormat.UEFormat or EMeshFormat.USD) ||
            AnimFormatOverride is not (null or EMeshFormat.ActorX or EMeshFormat.UEFormat or EMeshFormat.USD))
            throw new UserFacingException("The export recipe has an unsupported mode, engine or format.");

        ValidateFilter(IncludeRegex, "include");
        ValidateFilter(ExcludeRegex, "exclude");
    }

    private static void ValidateFilter(string? pattern, string name)
    {
        try { _ = Cli.CompileFilter(pattern, name); }
        catch (UserFacingException)
        {
            throw new UserFacingException($"The export recipe has an invalid {name} filter.",
                "Use a valid regular expression and save the recipe again.");
        }
    }

    private static string[]? NormalizeSelection(IEnumerable<string>? paths)
    {
        if (paths is null) return null;

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            var normalized = path?.Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(normalized) || normalized.Any(char.IsControl) ||
                normalized.IndexOfAny(['<', '>', ':', '"', '|', '?', '*']) >= 0 ||
                normalized.Split('/').Any(segment => segment is "" or "." or ".."))
                throw new UserFacingException("The export recipe contains an invalid container entry path.",
                    "Use paths relative to the container, for example Game/Content/Example.uasset.");
            result.Add(normalized);
        }

        return result.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool ContainsKey(string? text, string key) =>
        text?.Contains(key, StringComparison.OrdinalIgnoreCase) == true;

    private static void CheckSize(long bytes)
    {
        if (bytes > MaximumFileSize)
            throw new UserFacingException("The export recipe is larger than 32 MiB.",
                "Use a filter to describe the selection instead of listing every entry.");
    }

    private static UserFacingException InvalidFormat() => new("The export recipe is not valid JSON versioned settings.",
        "Use a recipe saved by UEBulkExport. Local paths and AES keys belong in the normal export inputs.");
}
