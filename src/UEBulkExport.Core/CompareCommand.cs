using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using CUE4Parse.UE4.Versions;

namespace UEBulkExport;

public static class CompareCommand
{
    public const string Usage = """
        UEBulkExport.Cli compare --before <path> --after <path> [--hash] [--groups] [--json]
                                 [--save-manifest <file>]
        UEBulkExport.Cli compare --apply-manifest <file> --before <path> --after <path> --out <folder> [--dry-run]
          --before-game <version> / --after-game <version>  Override the detected engine profile.
          --before-aes <key> / --after-aes <key>              AES keys, repeatable for each build.
          --hash                                            Compare the bytes of equal-size entries using SHA-256.
          --groups                                          Show package groups and their complete after-file counts.
          --oodle <file> / --zlib <file>                     Optional compression libraries.
          --json                                            Write JSON to stdout and diagnostics to stderr.
          --save-manifest <file>                             Save relative group paths and archive fingerprints.
          --apply-manifest <file>                            Verify local archives and raw-export the recorded scope.
          --dry-run                                         Verify the manifest and show the plan without writing output.
        Paths can identify a container folder, a .pak, a .utoc, or its matching .ucas.
        The default compares paths and sizes. Equal sizes do not establish equal contents.
        Applying requires an after folder with unique virtual paths. Supply AES keys locally.
        """;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static bool WritesJson(string[] args)
    {
        if (args.Length == 0 || args[0] != "compare") return false;
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] == "--json") return true;
            if (args[i] is "--before" or "--after" or "--before-game" or "--after-game" or
                "--before-aes" or "--after-aes" or "--oodle" or "--zlib" or "--out" or
                "--save-manifest" or "--apply-manifest") i++;
        }
        return false;
    }

    internal static Options? Execute(string[] args, out CompareManifestVerification? verification,
        CancellationToken ct = default)
    {
        verification = null;
        string before = "", after = "";
        string? oodle = null, zlib = null, saveManifest = null, applyManifest = null, output = null;
        EGame? beforeGame = null, afterGame = null;
        List<string> beforeKeys = [], afterKeys = [];
        var hash = false;
        var json = false;
        var grouped = false;
        var dryRun = false;

        for (var i = 0; i < args.Length; i++)
        {
            var argument = args[i];
            string Next() => i + 1 < args.Length ? args[++i] :
                throw new UserFacingException($"{argument} needs a value.");

            switch (argument)
            {
                case "--help" or "-h" or "/?": Log.Raw(Usage); return null;
                case "--before": before = Next(); break;
                case "--after": after = Next(); break;
                case "--before-game": beforeGame = Cli.ParseGame(Next()); break;
                case "--after-game": afterGame = Cli.ParseGame(Next()); break;
                case "--before-aes": beforeKeys.Add(Next()); break;
                case "--after-aes": afterKeys.Add(Next()); break;
                case "--oodle": oodle = Next(); break;
                case "--zlib": zlib = Next(); break;
                case "--hash": hash = true; break;
                case "--json": json = true; break;
                case "--groups": grouped = true; break;
                case "--save-manifest": saveManifest = Next(); break;
                case "--apply-manifest": applyManifest = Next(); break;
                case "--out": output = Next(); break;
                case "--dry-run": dryRun = true; break;
                default: throw new UserFacingException("Unknown compare argument.", "Run compare --help for usage.");
            }
        }

        var beforeSource = new CompareSource(before, beforeGame, beforeKeys);
        var afterSource = new CompareSource(after, afterGame, afterKeys);
        if (applyManifest is not null)
        {
            if (saveManifest is not null || beforeGame is not null || afterGame is not null || hash || json || grouped)
                throw new UserFacingException("The manifest already supplies the comparison and engine profiles.",
                    "Run compare --help for the manifest application options.");
            if (string.IsNullOrWhiteSpace(output))
                throw new UserFacingException("--out is required when applying a comparison manifest.");
            verification = CompareManifest.Load(applyManifest).Verify(beforeSource, afterSource, oodle, zlib, ct: ct);
            return PrepareExport(verification, afterKeys, output, oodle, zlib, dryRun);
        }

        if (output is not null || dryRun)
            throw new UserFacingException("Use --out and --dry-run with --apply-manifest.");

        var result = CompareBuilds.Run(new(beforeSource, afterSource, hash, oodle, zlib), ct: ct);
        if (saveManifest is not null) CompareManifest.Capture(result, ct: ct).Save(saveManifest);

        if (json)
        {
            Log.Raw(JsonSerializer.Serialize(result, JsonOptions));
            return null;
        }

        if (grouped)
        {
            Log.Raw("Change\tKind\tPath\tChangedEntries\tAfterFiles");
            foreach (var group in result.Groups)
            {
                ct.ThrowIfCancellationRequested();
                Log.Raw($"{group.Change}\t{group.Kind}\t{group.Path}\t{group.Changes.Count}\t{group.AfterPaths.Count}");
            }
        }
        else
        {
            Log.Raw("Change\tPath\tBeforeSize\tAfterSize");
            foreach (var entry in result.Entries)
            {
                ct.ThrowIfCancellationRequested();
                Log.Raw($"{entry.Change}\t{entry.Path}\t{entry.BeforeSize?.ToString(CultureInfo.InvariantCulture)}\t" +
                        entry.AfterSize?.ToString(CultureInfo.InvariantCulture));
            }
        }
        Log.Raw($"Added: {result.Added}, removed: {result.Removed}, changed: {result.Changed}, " +
                $"{(hash ? "unchanged" : "same size")}: {result.Unchanged}");
        return null;
    }

    private static Options PrepareExport(CompareManifestVerification verification, List<string> keys, string output,
        string? oodle, string? zlib, bool dryRun)
    {
        if (!verification.Result.CanExportAfter)
            throw new UserFacingException("The after archive set cannot be exported by exact path.",
                "Compare using an after folder whose containers have unique virtual paths.");
        if (verification.SelectedPaths.Count == 0)
            throw new UserFacingException("The manifest has no after files to export.",
                "Removed groups only describe what was absent from the after build.");

        var options = new Options
        {
            PaksDirectory = verification.Result.AfterDirectory,
            Game = verification.Result.AfterGame,
            AesKeys = keys,
            OutputDirectory = output,
            OodlePath = oodle,
            ZlibPath = zlib,
            Mode = ExportMode.Raw,
            DryRun = dryRun,
            SelectedPaths = verification.SelectedPaths
        };
        Cli.Prepare(options);
        return options;
    }
}
