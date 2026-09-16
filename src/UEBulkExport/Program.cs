using UEBulkExport;

var launchedFromExplorer = args.Length == 0 && !Console.IsOutputRedirected;

try
{
    var options = Cli.Parse(args);
    if (options is null) return WaitIfNeeded(0);

    if (!string.IsNullOrEmpty(options.OutputDirectory) && !options.DryRun)
    {
        Directory.CreateDirectory(options.OutputDirectory);
        Log.OpenFile(Path.Combine(options.OutputDirectory, "UEBulkExport.log"));
    }

    Log.Info($"UEBulkExport {Cli.Version} - {options.Mode} export");
    Log.Info($"containers: {options.PaksDirectory}");

    Natives.Initialize(options);

    using var exporter = new BulkExporter(options);
    exporter.Mount();

    var files = exporter.SelectFiles();

    if (options.Mode == ExportMode.List)
    {
        exporter.PrintListing(files);
        return WaitIfNeeded(0);
    }

    exporter.VerifyMappings(files);

    if (options.DryRun)
    {
        exporter.PrintPlan(files);
        return WaitIfNeeded(0);
    }

    return WaitIfNeeded(await exporter.RunAsync(files));
}
catch (UserFacingException e)
{
    Log.Problem(e.Headline, e.Hint);
    return WaitIfNeeded(1);
}
catch (Exception e)
{
    Log.Error(e.ToString());
    return WaitIfNeeded(1);
}
finally
{
    Log.Close();
}

// Double-clicking the executable in Explorer opens a console that closes the instant the process
// ends, which makes the help text useless. Hold it open when nobody is reading a redirected pipe.
int WaitIfNeeded(int exitCode)
{
    if (!launchedFromExplorer) return exitCode;

    Console.WriteLine();
    Console.WriteLine("Press any key to close...");

    try { Console.ReadKey(intercept: true); }
    catch (InvalidOperationException) { /* no console input available after all */ }

    return exitCode;
}
