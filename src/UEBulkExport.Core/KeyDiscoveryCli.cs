using System.Text.Json;
using CUE4Parse.UE4.Versions;

namespace UEBulkExport;

public sealed class KeyDiscoveryOptions
{
    public string SourcePath { get; set; } = "";
    public string? PaksDirectory { get; set; }
    public EGame Game { get; set; } = EGame.GAME_UE5_LATEST;
    public int MaxCandidates { get; set; } = 256;
    public bool ScanMachineCode { get; set; } = true;
    public bool ShowKeys { get; set; }
    public bool Json { get; set; }
    public bool VerifiedOnly { get; set; }
}

public sealed class KeyProbeOptions
{
    public string ExecutablePath { get; set; } = "";
    public List<string> Arguments { get; } = [];
    public string? PaksDirectory { get; set; }
    public EGame Game { get; set; } = EGame.GAME_UE5_LATEST;
    public int ListenSeconds { get; set; } = 20;
    public bool ShowKeys { get; set; }
    public bool Json { get; set; }
    public bool VerifiedOnly { get; set; }
}

public static class KeyDiscoveryCli
{
    public static bool IsCommand(IReadOnlyList<string> args) =>
        args.Count > 0 && args[0].Equals("keys", StringComparison.OrdinalIgnoreCase);

    public static async Task<int> RunAsync(string[] args, CancellationToken ct = default)
    {
        try
        {
            if (args.Length > 1 && args[1].Equals("probe", StringComparison.OrdinalIgnoreCase))
                return await RunProbeAsync(args, ct);

            var options = Parse(args);
            if (options is null) return 0;

            if (!options.Json)
                Log.Info($"scanning AES key source: {Path.GetFullPath(options.SourcePath)}");
            var scan = await AesKeyScanner.ScanAsync(
                options.SourcePath, options.MaxCandidates, options.ScanMachineCode, ct);

            IReadOnlyDictionary<string, AesKeyValidation>? validations = null;
            if (options.PaksDirectory is not null && scan.Candidates.Count > 0)
            {
                if (!options.Json)
                    Log.Info($"validating {scan.Candidates.Count} candidate(s) against selected containers");
                validations = await AesKeyValidator.ValidateAsync(
                    options.PaksDirectory, options.Game, scan.Candidates, ct);
            }

            var visible = options.VerifiedOnly && validations is not null
                ? scan.Candidates.Where(c => validations[c.Key].Verified).ToList()
                : scan.Candidates;

            if (options.Json)
                PrintJson(scan, visible, validations, options.ShowKeys);
            else
                PrintText(scan, visible, validations, options.ShowKeys);

            return visible.Count > 0 ? 0 : 2;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Log.Warn("key scan cancelled");
            return 2;
        }
        catch (UserFacingException e)
        {
            Log.Problem(e.Headline, e.Hint);
            return 1;
        }
        catch (Exception e)
        {
            Log.Error(e.ToString());
            return 1;
        }
    }

    private static async Task<int> RunProbeAsync(string[] args, CancellationToken ct)
    {
        var options = ParseProbe(args);
        if (options is null) return 0;

        if (!options.Json)
            Log.Info($"launching probe-enabled Unreal test build: {Path.GetFullPath(options.ExecutablePath)}");

        var probe = await CooperativeKeyProbe.CaptureAsync(
            options.ExecutablePath,
            options.Arguments,
            TimeSpan.FromSeconds(options.ListenSeconds),
            ct);

        IReadOnlyDictionary<string, AesKeyValidation>? validations = null;
        if (options.PaksDirectory is not null && probe.Candidates.Count > 0)
        {
            if (!options.Json)
                Log.Info($"validating {probe.Candidates.Count} reported key(s) against selected containers");
            validations = await AesKeyValidator.ValidateAsync(options.PaksDirectory, options.Game, probe.Candidates, ct);
        }

        var visible = options.VerifiedOnly && validations is not null
            ? probe.Candidates.Where(c => validations[c.Key].Verified).ToList()
            : probe.Candidates;
        var result = new AesKeyScanResult(probe.Candidates, 0, false);

        if (options.Json)
            PrintJson(result, visible, validations, options.ShowKeys, new
            {
                Mode = "cooperative-probe",
                probe.ProcessId,
                probe.Connected,
                probe.TimedOut
            });
        else
            PrintText(result, visible, validations, options.ShowKeys,
                $"Probe PID {probe.ProcessId}; connected={probe.Connected}; " +
                $"{probe.Candidates.Count} unique key(s) reported." +
                (probe.TimedOut ? " Listening window elapsed; the test process was left running." : ""));

        return visible.Count > 0 ? 0 : 2;
    }

    public static KeyDiscoveryOptions? Parse(string[] args)
    {
        if (!IsCommand(args))
            throw new ArgumentException("Not a key discovery command.", nameof(args));

        if (args.Length == 1 || args.Skip(1).Any(a => a is "--help" or "-h" or "/?"))
        {
            Log.Raw(Usage);
            return null;
        }

        if (!args[1].Equals("scan", StringComparison.OrdinalIgnoreCase))
            throw new UserFacingException($"Unknown keys command '{args[1]}'.",
                "Use 'keys scan --source <file>' or 'keys probe --exe <file>'.");

        var options = new KeyDiscoveryOptions();
        for (var i = 2; i < args.Length; i++)
        {
            var argument = args[i];
            string Next()
            {
                if (++i >= args.Length) throw new UserFacingException($"{argument} needs a value.");
                return args[i];
            }

            switch (argument)
            {
                case "--source": options.SourcePath = Next(); break;
                case "--paks": options.PaksDirectory = Next(); break;
                case "--game": options.Game = Cli.ParseGame(Next()); break;
                case "--max-candidates":
                    if (!int.TryParse(Next(), out var max) || max is < 1 or > 4096)
                        throw new UserFacingException("--max-candidates expects a number from 1 to 4096.");
                    options.MaxCandidates = max;
                    break;
                case "--no-code-patterns": options.ScanMachineCode = false; break;
                case "--show-keys": options.ShowKeys = true; break;
                case "--json": options.Json = true; break;
                case "--verified-only": options.VerifiedOnly = true; break;
                default:
                    throw new UserFacingException($"Unknown keys scan argument '{argument}'.", "Run UEBulkExport.Cli keys --help.");
            }
        }

        if (string.IsNullOrWhiteSpace(options.SourcePath))
            throw new UserFacingException("--source is required.",
                "Select an executable, DLL, Crypto.json, or a dump file that you own.");
        if (!File.Exists(options.SourcePath))
            throw new UserFacingException($"Key source file not found: {options.SourcePath}");
        if (options.VerifiedOnly && options.PaksDirectory is null)
            throw new UserFacingException("--verified-only requires --paks.");
        if (options.PaksDirectory is not null)
            options.PaksDirectory = Discovery.ResolvePaksDirectory(options.PaksDirectory);

        return options;
    }

    public static KeyProbeOptions? ParseProbe(string[] args)
    {
        if (!IsCommand(args) || args.Length < 2 || !args[1].Equals("probe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Not a cooperative key probe command.", nameof(args));

        if (args.Skip(2).Any(a => a is "--help" or "-h" or "/?"))
        {
            Log.Raw(Usage);
            return null;
        }

        var options = new KeyProbeOptions();
        for (var i = 2; i < args.Length; i++)
        {
            var argument = args[i];
            string Next()
            {
                if (++i >= args.Length) throw new UserFacingException($"{argument} needs a value.");
                return args[i];
            }

            switch (argument)
            {
                case "--exe": options.ExecutablePath = Next(); break;
                case "--arg": options.Arguments.Add(Next()); break;
                case "--paks": options.PaksDirectory = Next(); break;
                case "--game": options.Game = Cli.ParseGame(Next()); break;
                case "--wait-seconds":
                    if (!int.TryParse(Next(), out var seconds) || seconds is < 1 or > 600)
                        throw new UserFacingException("--wait-seconds expects a number from 1 to 600.");
                    options.ListenSeconds = seconds;
                    break;
                case "--show-keys": options.ShowKeys = true; break;
                case "--json": options.Json = true; break;
                case "--verified-only": options.VerifiedOnly = true; break;
                default:
                    throw new UserFacingException($"Unknown keys probe argument '{argument}'.",
                        "Run UEBulkExport.Cli keys --help.");
            }
        }

        if (string.IsNullOrWhiteSpace(options.ExecutablePath))
            throw new UserFacingException("--exe is required.",
                "Select your probe-enabled Unreal test executable.");
        if (!File.Exists(options.ExecutablePath))
            throw new UserFacingException($"Probe-enabled test executable not found: {options.ExecutablePath}");
        if (options.VerifiedOnly && options.PaksDirectory is null)
            throw new UserFacingException("--verified-only requires --paks.");
        if (options.PaksDirectory is not null)
            options.PaksDirectory = Discovery.ResolvePaksDirectory(options.PaksDirectory);

        return options;
    }

    private static void PrintText(
        AesKeyScanResult scan,
        IReadOnlyList<AesKeyCandidate> candidates,
        IReadOnlyDictionary<string, AesKeyValidation>? validations,
        bool showKeys,
        string? summary = null)
    {
        Log.Raw(summary ?? $"Scanned {scan.BytesScanned:N0} bytes; {scan.Candidates.Count} unique candidate(s)." +
            (scan.Truncated ? " Candidate limit reached; results are truncated." : ""));

        foreach (var candidate in candidates)
        {
            var status = validations is null ? "candidate" : validations[candidate.Key].Verified ? "VERIFIED" : "rejected";
            var value = showKeys ? candidate.Key : $"sha256:{candidate.Fingerprint}";
            var validation = validations is not null && validations[candidate.Key].Verified
                ? $", unlocked={validations[candidate.Key].ContainersUnlocked}, files+={validations[candidate.Key].FilesAdded}"
                : "";
            var location = candidate.Offset >= 0 ? $"0x{candidate.Offset:X}" : "runtime";
            var guid = candidate.ContainerGuid is null ? "" : $", guid={candidate.ContainerGuid}";
            Log.Raw($"[{status}] {value} @ {location} " +
                    $"({candidate.Origin}, confidence={candidate.Confidence}{guid}{validation})");
        }

        if (candidates.Count > 0 && !showKeys)
            Log.Raw("Keys are redacted. Re-run with --show-keys only in a private terminal when you need the full value.");
        if (candidates.Count == 0)
            Log.Raw("No matching AES-256 key candidates were found.");
    }

    private static void PrintJson(
        AesKeyScanResult scan,
        IReadOnlyList<AesKeyCandidate> candidates,
        IReadOnlyDictionary<string, AesKeyValidation>? validations,
        bool showKeys,
        object? metadata = null)
    {
        var payload = new
        {
            scan.BytesScanned,
            scan.Truncated,
            CandidateCount = scan.Candidates.Count,
            Metadata = metadata,
            Results = candidates.Select(candidate =>
            {
                AesKeyValidation? validation = null;
                if (validations is not null && validations.TryGetValue(candidate.Key, out var found))
                    validation = found;
                return new
                {
                    Key = showKeys ? candidate.Key : null,
                    Fingerprint = candidate.Fingerprint,
                    candidate.Offset,
                    Origin = candidate.Origin.ToString(),
                    candidate.Evidence,
                    candidate.Confidence,
                    candidate.ContainerGuid,
                    Validation = validation
                };
            })
        };

        Log.Raw(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    }

    public const string Usage = """
        UEBulkExport AES key discovery

        USAGE
          UEBulkExport.Cli keys scan --source <file> [options]
          UEBulkExport.Cli keys probe --exe <file> [options]

        OPTIONS
          --source <file>       Executable, DLL, Crypto.json, or dump file to scan.
          --paks <path>         Verify candidates by mounting encrypted .pak/.utoc containers.
          --game <version>      Container profile used for verification (default: UE5 latest).
          --max-candidates <n>  Stop after n unique candidates, 1..4096 (default: 256).
          --no-code-patterns    Scan encoded text only; skip x86/x64 immediate-store patterns.
          --verified-only       Print only candidates that unlock a selected container; needs --paks.
          --show-keys           Print full secrets. By default only non-secret fingerprints are shown.
          --json                Machine-readable output.

        COOPERATIVE PROBE OPTIONS
          --exe <file>          Probe-enabled Unreal test executable to launch.
          --arg <value>         Exact argument passed to the test build. Repeatable.
          --wait-seconds <n>    Collection window, 1..600 (default: 20). The game is not killed.
          --paks/--game         Optionally verify reported keys against matching containers.
          --verified-only       Show only keys that unlock those containers; needs --paks.
          --show-keys           Reveal full keys instead of non-secret fingerprints.
          --json                Machine-readable output.

        Offline scan never opens another process. Cooperative probe only launches a test build
        that contains the UEBulkExportKeyProbe plugin and voluntarily reports its own registration
        events. Neither mode attaches, injects code, reads process memory, bypasses anti-cheat, or
        acquires a dump. Use them only with builds and files you own or are authorised to analyse.
        """;
}
