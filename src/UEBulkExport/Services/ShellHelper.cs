using System.Diagnostics;

namespace UEBulkExport.Gui.Services;

/// <summary>Hands paths and links to the operating system shell. Never throws into the UI.</summary>
public static class ShellHelper
{
    public static void OpenFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
        Start(path);
    }

    public static void OpenFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        Start(path);
    }

    public static void OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return;
        Start(url);
    }

    public static bool TryRestartApplication()
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable)) return false;

        try
        {
            Process.Start(new ProcessStartInfo(executable)
            {
                UseShellExecute = true,
                WorkingDirectory = AppContext.BaseDirectory
            });
            return true;
        }
        catch (Exception e)
        {
            Log.Warn($"could not restart '{executable}': {e.Message}");
            return false;
        }
    }

    private static void Start(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            Log.Warn($"could not open '{target}': {e.Message}");
        }
    }
}
