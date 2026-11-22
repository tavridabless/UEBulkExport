namespace UEBulkExport;

internal static class BlueprintCommand
{
    public static async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        if (args.Length > 0 && args[0] == "restore")
            return await RestoreAsync(args[1..], ct);
        if (args.Length > 0 && args[0] == "dump")
            return await BlueprintDumpCommand.RunAsync(args[1..], ct);
        if (args.Length == 0 || args[0] is "--help" or "-h")
        {
            Log.Raw("""
                UEBulkExport.Cli blueprint [--report <json>] [--dependency-map <json>]
                    [--plan <json>] [--linked-plan <json>] [--batch-plan <json>] [--class <object-path>]
                    [--max-packages <n>] [--max-instructions <n>] --paks <path> [source options]
                  Source options include --game, --aes, --usmap, --include, --exclude and --paths-file.
                  Put blueprint-specific flags before the source options.
                  --plan targets one class. --linked-plan adds supported Blueprint call targets from the scan.
                  --batch-plan includes all eligible scanned classes and reports recovery coverage.

                UEBulkExport.Cli blueprint dump --help
                UEBulkExport.Cli blueprint restore --help
                """);
            return 0;
        }

        var limits = new BlueprintRecoveryLimits();
        string? reportPath = null, mapPath = null, planPath = null, linkedPlanPath = null, batchPlanPath = null, classPath = null;
        var index = 0;
        while (index < args.Length && args[index] is
               ("--report" or "--dependency-map" or "--plan" or "--linked-plan" or "--batch-plan" or "--class" or
                "--max-packages" or "--max-instructions"))
        {
            var flag = args[index++];
            if (index >= args.Length) throw new UserFacingException($"{flag} needs a value.");
            var value = args[index++];
            switch (flag)
            {
                case "--report": reportPath = value; break;
                case "--dependency-map": mapPath = value; break;
                case "--plan": planPath = value; break;
                case "--linked-plan": linkedPlanPath = value; break;
                case "--batch-plan": batchPlanPath = value; break;
                case "--class": classPath = value; break;
                case "--max-packages": limits = limits with { MaxPackages = Number(value, flag) }; break;
                case "--max-instructions": limits = limits with { MaxInstructions = Number(value, flag) }; break;
            }
        }

        if (classPath is not null && planPath is null && linkedPlanPath is null)
            throw new UserFacingException("Use --class together with --plan or --linked-plan.");
        var outputPaths = new[] { reportPath, mapPath, planPath, linkedPlanPath, batchPlanPath }
            .OfType<string>().Select(Path.GetFullPath).ToArray();
        var pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        if (outputPaths.Distinct(pathComparer).Count() != outputPaths.Length)
            throw new UserFacingException("Choose separate paths for the report, dependency map and reconstruction plans.");

        var source = Cli.ParseArguments(["--mode", "full", .. args[index..]]);
        var report = BlueprintRecovery.Inspect(source, limits, ct);
        if (reportPath is not null) BlueprintRecovery.WriteReport(report, reportPath);
        if (mapPath is not null) BlueprintRecovery.WriteDependencyMap(report, mapPath);
        Log.Raw($"Inspected {report.PackagesInspected}/{report.PackagesSelected} packages. Found {report.Classes.Count} cooked Blueprint classes.");
        foreach (var item in report.Classes) Log.Raw($"{item.Path}\t{item.Functions.Count} functions");
        if (report.PackageLimitReached || report.InstructionLimitReached || report.MetadataLimitReached)
            Log.Raw("The report reached an inspection limit. Narrow the source filter or raise the relevant budget.");
        if (report.PackagesSkipped > 0 || report.PackagesFailed > 0 || report.LockedContainers > 0)
            Log.Raw($"Packages skipped: {report.PackagesSkipped}; read failures: {report.PackagesFailed}; locked containers: {report.LockedContainers}.");
        if (report.IncompleteClasses > 0 || report.IncompleteFunctions > 0 || report.DiagnosticsOmitted > 0)
            Log.Raw($"Incomplete classes: {report.IncompleteClasses}; incomplete functions: {report.IncompleteFunctions}; omitted diagnostics: {report.DiagnosticsOmitted}.");

        if (planPath is not null || linkedPlanPath is not null)
        {
            var item = classPath is null
                ? report.Classes.Count == 1 ? report.Classes[0] : null
                : report.Classes.FirstOrDefault(item => item.Path.Equals(classPath, StringComparison.OrdinalIgnoreCase));
            if (item is null)
                throw new UserFacingException("A reconstruction plan needs exactly one selected Blueprint class.",
                    "Narrow --include or specify its exact object path using --class.");
            ct.ThrowIfCancellationRequested();
            if (planPath is not null)
            {
                BlueprintRecovery.WriteReconstruction(item, planPath);
                Log.Raw("Saved the reconstruction plan.");
            }
            if (linkedPlanPath is not null)
            {
                BlueprintRecovery.WriteLinkedReconstruction(report, item.Path, linkedPlanPath, ct);
                Log.Raw("Saved the linked reconstruction plan.");
            }
        }
        var complete = IsCompleteInspection(report);
        if (batchPlanPath is not null)
        {
            var batch = BlueprintRecovery.WriteBatchReconstruction(report, batchPlanPath, ct);
            Log.Raw($"Saved the batch reconstruction plan for {batch.ClassesIncluded}/{batch.ClassesInspected} classes.");
            Log.Raw($"Functions reconstructed: {batch.FunctionsReconstructed}; stubs: {batch.FunctionsStubbed}. " +
                    $"Events reconstructed: {batch.EventsReconstructed}; components: {batch.ComponentsReconstructed}; " +
                    $"component overrides: {batch.ComponentOverridesReconstructed}.");
            complete &= batch.ClassesIncluded > 0;
        }
        return complete ? 0 : 2;
    }

    internal static bool IsCompleteInspection(BlueprintRecoveryReport report) =>
        report.Classes.Count > 0 && report.PackagesFailed == 0 && report.PackagesSkipped == 0 && report.LockedContainers == 0 &&
        !report.PackageLimitReached && !report.InstructionLimitReached && !report.MetadataLimitReached &&
        report.IncompleteClasses == 0 && report.IncompleteFunctions == 0;

    internal static int Number(string value, string option) => int.TryParse(value, out var number)
        ? number : throw new UserFacingException($"{option} expects a whole number.");

    private static async Task<int> RestoreAsync(string[] args, CancellationToken ct)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h")
        {
            Log.Raw("""
                UEBulkExport.Cli blueprint restore --plan <json> --editor <UnrealEditor-Cmd.exe>
                    --project <target.uproject> [--destination /Game/RecoveredBlueprints] [--mesh-map <json>]
                  Creates editable Blueprint assets in a Windows UE 5.8 project with the Python Editor Script Plugin.
                  Resolves StaticMesh references in the target project, with optional exact path remapping.
                  The result distinguishes restored logic and stubs. Existing assets are left intact.
                """);
            return 0;
        }

        string? plan = null, editor = null, project = null, meshMap = null;
        var destination = "/Game/RecoveredBlueprints";
        for (var i = 0; i < args.Length; i++)
        {
            var option = args[i];
            string Value() => i + 1 < args.Length ? args[++i] :
                throw new UserFacingException($"{option} needs a value.");
            switch (option)
            {
                case "--plan": plan = Value(); break;
                case "--editor": editor = Value(); break;
                case "--project": project = Value(); break;
                case "--destination": destination = Value(); break;
                case "--mesh-map": meshMap = Value(); break;
                default: throw new UserFacingException("Unknown blueprint restore argument.", "Run blueprint restore --help.");
            }
        }
        if (plan is null || editor is null || project is null)
            throw new UserFacingException("Blueprint restoration needs --plan, --editor and --project.");
        var report = await BlueprintEditorRestore.RestoreAsync(plan, editor, project, destination, ct, meshMap);
        Log.Raw($"Blueprint reconstruction report: {report}");
        return 0;
    }
}
