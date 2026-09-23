using System.Runtime.InteropServices;
using Avalonia;
using UEBulkExport.Gui.Services;

namespace UEBulkExport.Gui;

internal static class Program
{
    /// <summary>
    /// Double-clicked: open the window. Started with arguments: behave exactly like the console
    /// executable, so the documented commands keep working against this file too. The GUI build
    /// has no console of its own, so it borrows the parent's when there is one.
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0) return RunCommandLine(args);

        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception e)
        {
            WriteCrashLog(e);
            throw;
        }
    }

    private static int RunCommandLine(string[] args)
    {
        var attached = ConsoleAttach.TryAttach();
        Log.AddSink(new PlainConsoleLogSink());

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            if (cancellation.IsCancellationRequested) return;
            e.Cancel = true;
            cancellation.Cancel();
        };

        var exitCode = CliRunner.RunAsync(args, cancellation.Token).GetAwaiter().GetResult();

        // Started with arguments from Explorer (a shortcut, a drag-and-drop): a fresh console was
        // created for the output and would vanish with the process, so hold it open.
        if (attached == ConsoleAttach.Result.Allocated)
        {
            Console.WriteLine();
            Console.WriteLine("Press any key to close...");
            try { Console.ReadKey(intercept: true); } catch (InvalidOperationException) { }
        }

        return exitCode;
    }

    /// <summary>A window-less process has nowhere to show an exception, so keep it where the user can find it.</summary>
    private static void WriteCrashLog(Exception e)
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UEBulkExport");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "crash.log"), $"----- {DateTime.Now:yyyy-MM-dd HH:mm:ss} -----{Environment.NewLine}{e}{Environment.NewLine}");
        }
        catch
        {
            // nothing left to report to
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}

/// <summary>Attaches to the parent console (a terminal), or creates one when there is none.</summary>
internal static class ConsoleAttach
{
    public enum Result { None, Attached, Allocated }

    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    public static Result TryAttach()
    {
        if (!OperatingSystem.IsWindows()) return Result.Attached; // stdio is inherited on Unix
        if (AttachConsole(AttachParentProcess)) return Result.Attached;
        return AllocConsole() ? Result.Allocated : Result.None;
    }
}

/// <summary>Console output without colours: the attached console may not be ours to recolour.</summary>
internal sealed class PlainConsoleLogSink : ILogSink
{
    private readonly Lock _gate = new();

    public void Write(in LogEntry entry)
    {
        lock (_gate) Console.WriteLine(entry.Format());
    }
}
