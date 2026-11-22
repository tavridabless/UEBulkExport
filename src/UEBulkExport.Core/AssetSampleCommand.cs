namespace UEBulkExport;

internal static class AssetSampleCommand
{
    public static async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h")
        {
            Log.Raw("Usage: UEBulkExport.Cli sample [--type all|texture|mesh|animation|audio|level|material]");
            Log.Raw("       [--per-type 1..5] [--max-packages 1..10000] --paks <path> [--out <parent>] [export options]");
            Log.Raw("Sample options go before the export options. A fresh sample folder holds the files and report.");
            Log.Raw("Exit 0 requires a successful sample quota for each requested type with no read or conversion failures.");
            return 0;
        }

        var request = new AssetSampleRequest();
        var index = 0;
        while (index < args.Length && args[index] is ("--type" or "--per-type" or "--max-packages"))
        {
            var flag = args[index++];
            if (index == args.Length) throw new UserFacingException($"{flag} needs a value.");
            var value = args[index++];
            request = flag switch
            {
                "--type" => request with { Type = ParseType(value) },
                "--per-type" => request with { PerType = ParseLimit(value, flag) },
                _ => request with { PackageLimit = ParseLimit(value, flag) }
            };
        }

        var options = Cli.ParseArguments(["--mode", "full", .. args[index..]]);
        var result = await AssetSampling.RunAsync(options, request, ct);
        foreach (var type in result.Coverage)
            Log.Raw($"{type.Type,-12} {type.Converted}/{type.Requested} converted, {type.Candidates} candidates, " +
                    $"{type.NoOutput} no output, {type.Failed} failed ({type.Status})");
        if (result.OmittedItems > 0 || result.OmittedErrorRows > 0)
            Log.Raw($"Details omitted: {result.OmittedItems} sample items, {result.OmittedErrorRows} error rows.");
        Log.Raw($"Sample report: {Path.Combine(result.OutputDirectory, AssetSampling.ReportName)}");
        return result.ExitCode;
    }

    private static int ParseLimit(string value, string flag) => int.TryParse(value, out var limit) ? limit
        : throw new UserFacingException($"{flag} needs a whole number.");

    private static AssetSampleType? ParseType(string value)
    {
        if (value.Equals("all", StringComparison.OrdinalIgnoreCase)) return null;
        foreach (var type in Enum.GetValues<AssetSampleType>())
            if (value.Equals(type.ToString(), StringComparison.OrdinalIgnoreCase)) return type;
        throw new UserFacingException("Unknown asset type.",
            "Use all, texture, mesh, animation, audio, level or material.");
    }
}
