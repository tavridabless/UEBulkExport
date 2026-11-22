using System.Text.Json;

namespace UEBulkExport;

internal static class ManagedSourceCommand
{
    public static int Run(string[] args, CancellationToken ct)
    {
        if (args.Length > 0 && args[0] == "decompile") return Decompile(args[1..], ct);
        if (args.Length == 0 || args[0] is "--help" or "-h")
        {
            Log.Raw("""
                UEBulkExport.Cli source --assembly <managed.dll> [--pdb <portable.pdb>] [--out <parent>] [--json]
                  Checks an embedded or matching sidecar Portable PDB for original C# source.
                  --out writes verified documents into a new folder, preserving their bytes.
                UEBulkExport.Cli source decompile --help
                """);
            return 0;
        }

        string? assembly = null, pdb = null, output = null;
        var json = false;
        for (var i = 0; i < args.Length; i++)
        {
            var argument = args[i];
            string Value() => i + 1 < args.Length ? args[++i] :
                throw new UserFacingException($"{argument} needs a value.");
            switch (argument)
            {
                case "--assembly": assembly = Value(); break;
                case "--pdb": pdb = Value(); break;
                case "--out": output = Value(); break;
                case "--json": json = true; break;
                default: throw new UserFacingException("Unknown source argument.", "Run source --help for usage.");
            }
        }
        if (string.IsNullOrWhiteSpace(assembly)) throw new UserFacingException("--assembly is required.");

        var report = ManagedSourceRecovery.Run(assembly, pdb, output, ct);
        if (json) Log.Raw(JsonSerializer.Serialize(report, ManagedSourceRecovery.Json));
        else
        {
            Log.Raw($"Symbols: {report.Symbols}. Verified C# documents: {report.Verified}/{report.Documents.Count}.");
            if (report.Verified == 0) Log.Raw("No checksum-verified embedded C# source was found.");
            if (report.OutputDirectory is { } directory) Log.Raw($"Recovered source: {directory}");
        }
        return report.Verified > 0 ? 0 : 2;
    }

    private static int Decompile(string[] args, CancellationToken ct)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h")
        {
            Log.Raw("""
                UEBulkExport.Cli source decompile --assembly <managed.dll|exe> --out <parent>
                    [--reference-dir <folder>]... [--json]
                  Reconstructs editable C# from managed IL into a new SDK project folder without PDBs.
                  For build, install .NET SDK 10 or later and the target framework packs.
                  Repeat --reference-dir for local managed dependencies outside the assembly folder.
                """);
            return 0;
        }
        string? assembly = null, output = null;
        var references = new List<string>();
        var json = false;
        for (var i = 0; i < args.Length; i++)
        {
            var argument = args[i];
            string Value() => i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[++i] : throw new UserFacingException($"{argument} needs a value.");
            switch (argument)
            {
                case "--assembly": assembly = Value(); break;
                case "--out": output = Value(); break;
                case "--reference-dir": references.Add(Value()); break;
                case "--json": json = true; break;
                default: throw new UserFacingException("Unknown decompile argument.", "Run source decompile --help for usage.");
            }
        }
        if (string.IsNullOrWhiteSpace(assembly) || string.IsNullOrWhiteSpace(output))
            throw new UserFacingException("source decompile requires --assembly and --out.");
        var report = ManagedSourceDecompilation.Run(assembly, output, references, ct);
        if (json) Log.Raw(JsonSerializer.Serialize(report, ManagedSourceRecovery.Json));
        else
        {
            Log.Raw($"Decompiled project: {report.ProjectPath} ({report.TargetFramework})");
            foreach (var reference in report.UnresolvedReferences) Log.Raw($"Unresolved reference: {reference}");
            foreach (var diagnostic in report.Diagnostics) Log.Raw(diagnostic);
        }
        return report.HasGaps ? 2 : 0;
    }
}
