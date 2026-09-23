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
