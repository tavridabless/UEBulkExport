using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Versions;
using CUE4Parse_Conversion.Options;

namespace UEBulkExport;

public enum ExportMode
{
    /// <summary>Extract legacy-format cooked packages; convert IoStore packages with retoc.</summary>
    Legacy,

    /// <summary>Parse every package and convert it to usable formats. Needs mappings.</summary>
    Full,

    /// <summary>Byte-for-byte dump of every container entry. No parsing, no mappings needed.</summary>
    Raw,

    /// <summary>Only the .json property dumps. Needs mappings.</summary>
    Json,

    /// <summary>Print what the container holds and exit.</summary>
    List
}

public sealed class Options
{
    public string PaksDirectory { get; set; } = "";
    public string OutputDirectory { get; set; } = "";
    public string? UsmapPath { get; set; }
    public string? OodlePath { get; set; }
    public string? ZlibPath { get; set; }
    public string? VgmStreamPath { get; set; }
    public EGame Game { get; set; } = EGame.GAME_UE5_3;
    public ETexturePlatform Platform { get; set; } = ETexturePlatform.DesktopMobile;
    public ExportMode Mode { get; set; } = ExportMode.Legacy;
    public List<string> AesKeys { get; set; } = [];
    public int Threads { get; set; } = Math.Max(1, Environment.ProcessorCount - 1);

    public string? IncludeRegex { get; set; }
    public string? ExcludeRegex { get; set; }

    /// <summary>A text file with one container path per line: only those entries are exported.</summary>
    public string? PathsFile { get; set; }

    /// <summary>The resolved contents of <see cref="PathsFile"/>, or a set handed over directly by the GUI.</summary>
    public IReadOnlySet<string>? SelectedPaths { get; set; }

    public bool WriteJson { get; set; } = true;
    public bool WriteAssets { get; set; } = true;
    public bool WriteRawMisc { get; set; } = true;
    public bool WriteRawPackages { get; set; }
    public bool ExportMaterials { get; set; }
    public bool ConvertAudio { get; set; } = true;
    public bool ExportWorlds { get; set; } = true;
    public bool Resume { get; set; } = true;
    public bool Verbose { get; set; }
    public bool DryRun { get; set; }
    public string? RetocPath { get; set; }

    public EMeshFormat MeshFormat { get; set; } = EMeshFormat.Gltf2;
    public EMeshFormat? AnimFormatOverride { get; set; }
    public ENaniteMeshFormat NaniteMeshFormat { get; set; } = ENaniteMeshFormat.NoNanite;
    public EMeshQuality MeshQuality { get; set; } = EMeshQuality.Highest;
    public ETextureFormat TextureFormat { get; set; } = ETextureFormat.Png;
    public ESocketFormat SocketFormat { get; set; } = ESocketFormat.Bone;
    public bool ExportMorphTargets { get; set; } = true;
    public bool ExportAllTextureMips { get; set; }

    /// <summary>Modes that have to deserialize packages, and therefore need a mappings file.</summary>
    public bool RequiresMappings => Mode is ExportMode.Full or ExportMode.Json;

    /// <summary>
    /// glTF has no representation for a bare animation track or a skeleton on its own, so those
    /// fall back to ActorX (.psa/.psk) unless told otherwise.
    /// </summary>
    public EMeshFormat AnimFormat =>
        AnimFormatOverride ?? (MeshFormat == EMeshFormat.Gltf2 ? EMeshFormat.ActorX : MeshFormat);

    public ExportOptions ToExportOptions() => BuildExportOptions(MeshFormat);

    public ExportOptions ToAnimExportOptions() => BuildExportOptions(AnimFormat);

    /// <summary>USD is the only format CUE4Parse can write a level into.</summary>
    public ExportOptions ToWorldExportOptions() => BuildExportOptions(EMeshFormat.USD);

    private ExportOptions BuildExportOptions(EMeshFormat meshFormat) => new(
        meshFormat: meshFormat,
        naniteMeshFormat: NaniteMeshFormat,
        meshQuality: MeshQuality,
        texturePlatform: Platform,
        textureFormat: TextureFormat,
        textureQuality: 100,
        exportHdrTexturesAsHdr: true,
        exportAllTextureMips: ExportAllTextureMips,
        exportMaterials: ExportMaterials,
        exportMorphTargets: ExportMorphTargets,
        socketFormat: SocketFormat);

    public const string Usage = """
        UEBulkExport - export an entire Unreal Engine container (.utoc/.ucas/.pak) into one folder
        Run UEBulkExport.exe without arguments to open the graphical interface.

        USAGE
          UEBulkExport.Cli --paks <path> --out <dir> [options]
          UEBulkExport --paks <path> --out <dir> [options]      (the GUI build accepts the same arguments)

        PATHS
          --paks <path>         Where the containers are. Accepts the Paks folder itself, any
                                folder above it (the game's root works), or a single .utoc file.
          --out <dir>           Destination folder. The container tree is mirrored inside it.
          --usmap <file>        Mappings file for full/json modes. Auto-detected when unique.

        CORE OPTIONS
          --mode <m>            legacy (default) | full | raw | json | list
                                  legacy - cooked .uasset/.umap and payloads; IoStore is
                                           converted to legacy format with retoc (no mappings)
                                  full   - parse and convert everything        (needs mappings)
                                  json   - property dumps only                 (needs mappings)
                                  raw    - byte-exact dump of every entry      (no mappings)
                                  list   - print container contents and exit   (no mappings)
                                Cooked assets are not restored to original editable assets.
                                Unreal Editor can use only supported cooked types, read-only;
                                use the same engine version that cooked them. Opening them in an
                                asset editor is not supported.
          --game <version>      Engine version, default GAME_UE5_3.
                                Accepts "5.3", "UE5_3" or "GAME_UE5_3".
          --aes <0x...>         AES key for encrypted containers. Repeatable.
                                Use --aes <guid>:<0x...> to tie a key to one container.
          --threads <n>         Worker threads, default = CPU count - 1.
          --dry-run             Report what would be exported and write nothing.
          --retoc <file>        retoc executable for IoStore-to-legacy conversion. On Windows
                                x64, a verified copy is downloaded if one is not supplied.

        FILTERING
          --include <regex>     Only export entries whose container path matches.
          --exclude <regex>     Skip entries whose container path matches.
                                e.g. --exclude "^Engine/" to drop stock engine content.
          --paths-file <file>   Export only the entries listed in the file, one container path
                                per line (the GUI writes _selection.txt for its selections).

        WHAT TO WRITE (mode=full)
          --no-json             Skip the .json property dumps.
          --no-assets           Skip mesh/texture/audio/animation conversion.
          --no-raw-misc         Skip loose non-package files (.ini, .png, .ttf, .mp4 ...).
          --no-worlds           Skip .umap levels. Levels are written as USD - the only level
                                format available - and they are large.
          --no-audio-convert    Keep BINKA/ADPCM audio as-is instead of also writing a .wav.
          --raw-packages        Additionally dump the untouched .uasset/.ubulk bytes.
          --materials           Export materials as assets and let meshes pull in the materials
                                and textures they reference. For materials this replaces the
                                property dump, since both claim the same .json name.
          --overwrite           Re-export entries already recorded as done (default: resume).

        FORMATS
          --mesh <f>            gltf2 (default) | actorx | ueformat | usd
          --anim <f>            actorx (default) | ueformat | usd
                                Animations and standalone skeletons. glTF cannot carry either
                                on its own, so it is not valid here.
          --texture <f>         png (default) | tga | jpeg | webp
          --quality <q>         highest (default) | lowest | all    (which mesh LODs to write)
          --nanite <n>          nonanite (default) | naniteonly | nanitefirst | nanitelast
          --sockets <f>         bone (default) | socket | none
          --no-morphs           Skip morph targets on skeletal meshes.
          --all-mips            Write every texture mip, not just the largest.
          --platform <p>        DesktopMobile (default) | XboxAndPlaystation4 | Playstation5 | NintendoSwitch

        HELPER BINARIES
          --oodle <file>        Oodle library. Auto-detected, else downloaded on first run.
          --zlib <file>         zlib-ng library. Same treatment.
          --vgmstream <file>    vgmstream-cli, used to turn BINKA/ADPCM audio into .wav.

        MISC
          --verbose             Log every file written.
          --version             Print the version and exit.
          --help                This text.

        EXAMPLES
          # What is in there? Works without mappings.
          UEBulkExport.Cli --paks "D:\Games\MyGame" --out "D:\Export" --mode list

          # Extract cooked .uasset/.umap packages and their payloads; no JSON or usmap.
          UEBulkExport.Cli --paks "D:\Games\MyGame" --out "D:\Export"

          # Convert to viewable formats. Mappings picked up if one .usmap is nearby.
          UEBulkExport.Cli --paks "D:\Games\MyGame" --out "D:\Converted" --mode full

          # Game content only, no stock engine assets, no levels (full mode).
          UEBulkExport.Cli --paks "D:\Games\MyGame" --out "D:\Export" ^
                               --mode full --exclude "^Engine/" --no-worlds

          # Byte-exact dump. No mappings required.
          UEBulkExport.Cli --paks "D:\Games\MyGame" --out "D:\Export\raw" --mode raw
        """;
}
