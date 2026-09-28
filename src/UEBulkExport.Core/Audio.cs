using System.Diagnostics;

namespace UEBulkExport;

internal enum AudioConversionStatus { Unavailable, Converted, Failed }

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

    internal static async Task<AudioConversionStatus> TryConvertToWavAsync(string sourcePath, string wavPath,
        CancellationToken ct)
    {
        if (_vgmStream is null) return AudioConversionStatus.Unavailable;

        Directory.CreateDirectory(Path.GetDirectoryName(wavPath)!);
        var temporary = AtomicFile.CreateTemporaryPath(wavPath);

        var info = new ProcessStartInfo(_vgmStream)
        {
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        info.ArgumentList.Add("-o");
        info.ArgumentList.Add(temporary);
        info.ArgumentList.Add(sourcePath);

        Process? process = null;
        try
        {
            process = Process.Start(info);
            if (process is null) return AudioConversionStatus.Failed;

            // vgmstream is quick, but a malformed stream can make it sit there indefinitely.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));

            await process.WaitForExitAsync(timeout.Token);
            if (process.ExitCode != 0 || !File.Exists(temporary))
            {
                Log.Warn($"vgmstream failed on {Path.GetFileName(sourcePath)} (exit {process.ExitCode})");
                return AudioConversionStatus.Failed;
            }

            AtomicFile.Publish(temporary, wavPath);
            return AudioConversionStatus.Converted;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Stop(process);
            throw;
        }
        catch (OperationCanceledException)
        {
            Stop(process);
            Log.Warn($"vgmstream timed out on {Path.GetFileName(sourcePath)}");
            return AudioConversionStatus.Failed;
        }
        catch (Exception e)
        {
            Stop(process);
            Log.Warn($"vgmstream failed on {Path.GetFileName(sourcePath)}: {e.Message}");
            return AudioConversionStatus.Failed;
        }
        finally
        {
            process?.Dispose();
            AtomicFile.TryDelete(temporary);
        }
    }

    private static void Stop(Process? process)
    {
        try
        {
            if (process is not { HasExited: false }) return;
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
