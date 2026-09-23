using System.Diagnostics;

namespace UEBulkExport;

/// <summary>
/// Shipping Unreal builds usually store audio as Bink (.binka) or ADPCM, neither of which most
/// tools can open. vgmstream turns those into plain .wav when it is available.
/// </summary>
public static class Audio
{
    private static readonly string[] ToolNames = ["vgmstream-cli.exe", "vgmstream-cli"];

    private static string? _vgmStream;

    public static void Initialize(Options options, IEnumerable<string> searchDirectories)
    {
        if (!string.IsNullOrEmpty(options.VgmStreamPath))
        {
            _vgmStream = options.VgmStreamPath;
            Log.Info($"vgmstream: {_vgmStream}");
            return;
        }

        _vgmStream = searchDirectories
            .Where(d => !string.IsNullOrEmpty(d) && Directory.Exists(d))
            .SelectMany(d => ToolNames.Select(n => Path.Combine(d, n)))
            .FirstOrDefault(File.Exists);

        if (_vgmStream is not null)
            Log.Info($"vgmstream: {_vgmStream}");
        else if (options.ConvertAudio)
            Log.Warn("vgmstream not found - BINKA/ADPCM audio is written in its original form. " +
                     "Pass --vgmstream to also get .wav files.");
    }

    public static async Task<bool> TryConvertToWavAsync(string sourcePath, string wavPath, bool skipExisting,
        CancellationToken ct)
    {
        if (_vgmStream is null) return false;
        if (skipExisting && File.Exists(wavPath)) return false;

        var info = new ProcessStartInfo(_vgmStream)
        {
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        info.ArgumentList.Add("-o");
        info.ArgumentList.Add(wavPath);
        info.ArgumentList.Add(sourcePath);

        try
        {
            using var process = Process.Start(info);
            if (process is null) return false;

            // vgmstream is quick, but a malformed stream can make it sit there indefinitely.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));

            await process.WaitForExitAsync(timeout.Token);
            return process.ExitCode == 0 && File.Exists(wavPath);
        }
        catch (Exception e)
        {
            Log.Warn($"vgmstream failed on {Path.GetFileName(sourcePath)}: {e.Message}");
            return false;
        }
    }
}
