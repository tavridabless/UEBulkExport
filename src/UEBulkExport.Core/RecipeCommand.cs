namespace UEBulkExport;

/// <summary>Adapts recipe subcommands to the same normal export pipeline used by the CLI.</summary>
public static class RecipeCommand
{
    private static readonly HashSet<string> LocalValues = new(StringComparer.Ordinal)
    {
        "--paks", "--out", "--aes", "--usmap", "--oodle", "--zlib", "--vgmstream", "--retoc"
    };

    private const string Usage = """
        UEBulkExport.Cli recipe save <file.json> [export options]
        UEBulkExport.Cli recipe run <file.json> --paks <source> --out <destination>
          [--aes <key>] [--usmap <file>] [--oodle <file>] [--zlib <file>]
          [--vgmstream <file>] [--retoc <file>] [--dry-run] [--verbose]

        Save stores portable options and the contents of an optional --paths-file. Set the local
        source, output, mappings, helpers and any AES key when running the recipe.
        """;

    /// <summary>Returns prepared options to run, or null when a recipe was saved or help was printed.</summary>
    public static Options? Execute(string[] args)
    {
        if (args is ["recipe"] or ["recipe", "--help" or "-h"] or
            ["recipe", "save" or "run", "--help" or "-h"])
        {
            Log.Raw(Usage);
            return null;
        }

        return args switch
        {
            ["recipe", "save", var path, .. var other] => Save(path, other),
            ["recipe", "run", var path, .. var other] => Run(path, other),
            _ => throw new UserFacingException("The recipe command needs 'save' or 'run' and a JSON file.", Usage)
        };
    }

    private static Options? Save(string path, string[] args)
    {
        var options = Cli.ParseArguments(args);
        if (options.PathsFile is { } selection)
        {
            try
            {
                options.SelectedPaths = Cli.ReadSelection(selection);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                throw new UserFacingException("The recipe's selection file could not be read.",
                    "Use --paths-file with an existing selection list.");
            }
        }

        ExportRecipe.Capture(options).Save(path);
        Log.Info($"Export recipe saved to {Path.GetFullPath(path)}.");
        return null;
    }

    private static Options Run(string path, string[] args)
    {
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (argument is "--dry-run" or "--verbose") continue;
            if (!LocalValues.Contains(argument))
                throw new UserFacingException("Recipe run accepts local inputs and runtime flags only.", Usage);
            if (++index == args.Length)
                throw new UserFacingException($"{argument} needs a value.");
        }

        var options = ExportRecipe.Load(path).CreateOptions();
        var local = Cli.ParseArguments(args);
        options.PaksDirectory = local.PaksDirectory;
        options.OutputDirectory = local.OutputDirectory;
        options.UsmapPath = local.UsmapPath;
        options.OodlePath = local.OodlePath;
        options.ZlibPath = local.ZlibPath;
        options.VgmStreamPath = local.VgmStreamPath;
        options.RetocPath = local.RetocPath;
        options.AesKeys = local.AesKeys;
        options.DryRun = local.DryRun;
        options.Verbose |= local.Verbose;
        Cli.Prepare(options);
        return options;
    }
}
