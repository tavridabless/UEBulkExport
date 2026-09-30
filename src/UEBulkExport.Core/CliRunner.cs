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
            var options = Cli.Parse(args);
            if (options is null) return 0;

            if (!string.IsNullOrEmpty(options.OutputDirectory) && !options.DryRun)
            {
                Directory.CreateDirectory(options.OutputDirectory);
                Log.OpenFile(Path.Combine(options.OutputDirectory, "UEBulkExport.log"));
            }

            Log.Info($"UEBulkExport {Cli.Version} - {options.Mode} export");
            Log.Info($"containers: {options.PaksDirectory}");

            Natives.Initialize(options);

            using var exporter = new BulkExporter(options);
            exporter.ProgressChanged += PrintProgress;
            exporter.Mount();

            var files = exporter.SelectFiles();

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

    private static void PrintProgress(ExportProgress p)
    {
        if (p.Phase == "done" || p.Processed == 0) return;

        var eta = p.Eta is { } e ? e.ToString(@"hh\:mm\:ss") : "--:--:--";
        Log.Raw($"  {p.Processed}/{p.Total} ({p.Fraction * 100:F1}%)  {p.RatePerSecond:F0}/s  " +
                $"written {p.Written}  failed {p.Failed}  eta {eta}");
    }
}
