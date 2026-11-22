namespace UEBulkExport;

/// <summary>
/// The whole command line run, from arguments to exit code. Shared by the console executable and
/// by the GUI executable when it is started with arguments, so both behave identically.
/// </summary>
public static class CliRunner
{
    /// <summary>Exit codes: 0 clean, 1 could not start or crashed, 2 finished with failed entries.</summary>
    public static async Task<int> RunAsync(string[] args, CancellationToken ct = default)
    {
        try
        {
            if (args.Length > 0 && args[0] == "sample") return await AssetSampleCommand.RunAsync(args[1..], ct);
            if (args.Length > 0 && args[0] == "source") return ManagedSourceCommand.Run(args[1..], ct);
            if (args.Length > 0 && args[0] == "blueprint") return await BlueprintCommand.RunAsync(args[1..], ct);
            var isComparison = args.Length > 0 && args[0] == "compare";
            CompareManifestVerification? verification = null;
            var options = isComparison ? CompareCommand.Execute(args[1..], out verification, ct)
                : args.Length > 0 && args[0] == "recipe" ? RecipeCommand.Execute(args)
                : Cli.Parse(args);
            if (options is null) return 0;

            ct.ThrowIfCancellationRequested();

            var logToOutput = options.Mode != ExportMode.List && !string.IsNullOrEmpty(options.OutputDirectory) &&
                !options.DryRun;
            if (verification is null && logToOutput) OpenOutputLog(options.OutputDirectory);

            Log.Info($"UEBulkExport {Cli.Version} - {options.Mode} export");
            Log.Info($"containers: {options.PaksDirectory}");

            Natives.Initialize(options);

            using var exporter = new BulkExporter(options);
            exporter.ProgressChanged += PrintProgress;
            exporter.Mount();

            var files = exporter.SelectFiles(ct);
            verification?.CheckExportFiles(files);
            if (verification is not null && logToOutput) OpenOutputLog(options.OutputDirectory);

            if (options.Mode == ExportMode.List)
            {
                exporter.PrintListing(files);
                return 0;
            }

            exporter.VerifyMappings(files);

            if (options.DryRun)
            {
                exporter.PrintPlan(files);
                return 0;
            }

            var summary = await exporter.RunAsync(files, ct);
            return summary.ExitCode;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Log.Warn("cancelled");
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
        finally
        {
            Log.Close();
        }
    }

    private static void OpenOutputLog(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        Log.OpenFile(Path.Combine(outputDirectory, "UEBulkExport.log"));
    }

    private static void PrintProgress(ExportProgress p)
    {
        if (p.Phase == "done" || p.Processed == 0) return;

        var eta = p.Eta is { } e ? e.ToString(@"hh\:mm\:ss") : "--:--:--";
        Log.Raw($"  {p.Processed}/{p.Total} ({p.Fraction * 100:F1}%)  {p.RatePerSecond:F0}/s  " +
                $"written {p.Written}  failed {p.Failed}  eta {eta}");
    }
}
