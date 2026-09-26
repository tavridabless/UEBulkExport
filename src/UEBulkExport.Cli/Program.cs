using UEBulkExport;
using UEBulkExport.CommandLine;

var launchedFromExplorer = args.Length == 0 && !Console.IsOutputRedirected;

RunningMarker.Announce();
Log.AddSink(new ConsoleLogSink());

// Ctrl+C asks the exporter to stop; it then flushes the resume index and prints a summary, so
// the next run continues where this one was interrupted instead of losing the tail.
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    if (cancellation.IsCancellationRequested) return; // second Ctrl+C: let the process die
    e.Cancel = true;
    cancellation.Cancel();
};

var exitCode = await CliRunner.RunAsync(args, cancellation.Token);

// Double-clicking the executable in Explorer opens a console that closes the instant the process
// ends, which makes the help text useless. Hold it open when nobody is reading a redirected pipe.
if (launchedFromExplorer)
{
    Console.WriteLine();
    Console.WriteLine("Press any key to close...");

    try { Console.ReadKey(intercept: true); }
    catch (InvalidOperationException) { /* no console input available after all */ }
}

return exitCode;
