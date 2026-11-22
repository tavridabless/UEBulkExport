namespace UEBulkExport;

internal static class BlueprintDumpCommand
{
    public static async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h")
        {
            Log.Raw("""
                UEBulkExport.Cli blueprint dump --out <parent> --editor <UnrealEditor-Cmd.exe>
                    [--managed-dir <folder> [--decompile-csharp]] [--max-packages <n>] [--max-instructions <n>]
                    [--mesh-map <json>]
                    --paks <path> [source options]
                  Creates a fresh UE 5.8 project with editable Blueprint assets and recovery reports.
                  Transfers referenced /Game StaticMesh geometry and sockets from the source archives.
                  --managed-dir searches local assemblies for verified original C# in Portable PDBs.
                  --decompile-csharp generates a C# project when no original documents were recovered.
                  --mesh-map remaps StaticMesh references to assets available to the new project.
                  Put dump flags before the source options. Exit code 2 indicates gaps in the result.
                """);
            return 0;
        }

        string? output = null, editor = null, managedDirectory = null, meshMap = null;
        var decompile = false;
        var limits = new BlueprintRecoveryLimits(BlueprintRecovery.MaxPackageLimit, BlueprintRecovery.MaxInstructionLimit);
        var index = 0;
        while (index < args.Length && args[index] is
               ("--out" or "--editor" or "--managed-dir" or "--decompile-csharp" or "--mesh-map" or "--max-packages" or "--max-instructions"))
        {
            var flag = args[index++];
            if (flag == "--decompile-csharp")
            {
                decompile = true;
                continue;
            }
            if (index >= args.Length || args[index].StartsWith("--", StringComparison.Ordinal))
                throw new UserFacingException($"{flag} needs a value.");
            var value = args[index++];
            switch (flag)
            {
                case "--out": output = value; break;
                case "--editor": editor = value; break;
                case "--managed-dir": managedDirectory = value; break;
                case "--mesh-map": meshMap = value; break;
                case "--max-packages": limits = limits with { MaxPackages = BlueprintCommand.Number(value, flag) }; break;
                case "--max-instructions": limits = limits with { MaxInstructions = BlueprintCommand.Number(value, flag) }; break;
            }
        }
        if (string.IsNullOrWhiteSpace(output) || string.IsNullOrWhiteSpace(editor))
            throw new UserFacingException("Blueprint dump requires --out and --editor.");
        if (decompile && managedDirectory is null)
            throw new UserFacingException("Use --decompile-csharp together with --managed-dir.");
        var source = Cli.ParseArguments(["--mode", "full", .. args[index..]]);
        var result = await BlueprintProjectRecovery.RecoverAsync(source, limits, editor, output, managedDirectory,
            decompile, ct, meshMap);
        return result.HasGaps ? 2 : 0;
    }
}
