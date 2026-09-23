using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;

namespace UEBulkExport;

/// <summary>
/// retoc converts IoStore's Zen package layout to the traditional cooked .uasset/.uexp layout.
/// This is a format conversion, not a recovery of uncooked editor data.
/// </summary>
public static class Retoc
{
    private const string DownloadUrl =
        "https://github.com/trumank/retoc/releases/download/v0.1.5/retoc_cli-x86_64-pc-windows-msvc.zip";
    private const string DownloadSha256 =
        "CC036B06AD3BDCF7003690B00D82719980C374E48A95BF0654F9959148D263AA";
    private const string ExecutableSha256 =
        "A145F3557EC60B163ED1B78B2F380232D2753FA30350C8013C98E171DB3AE7E7";

    public static async Task<string> ResolveAsync(string? explicitPath, CancellationToken ct)
    {
        if (explicitPath is not null) return Path.GetFullPath(explicitPath);

        if (!OperatingSystem.IsWindows() || System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
            != System.Runtime.InteropServices.Architecture.X64)
            throw new UserFacingException(
                "IoStore legacy conversion needs retoc.",
                "Download retoc for this platform and pass its executable with --retoc <file>.");

        var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UEBulkExport", "native");
        var executable = Path.Combine(cache, "retoc-0.1.5.exe");
        if (File.Exists(executable) &&
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(executable))) == ExecutableSha256)
            return executable;

        Directory.CreateDirectory(cache);
        Log.Info("downloading retoc 0.1.5 for IoStore package conversion ...");

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            var archiveBytes = await client.GetByteArrayAsync(DownloadUrl, ct);
            var hash = Convert.ToHexString(SHA256.HashData(archiveBytes));
            if (!hash.Equals(DownloadSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("downloaded retoc archive failed its SHA-256 check");

            using var memory = new MemoryStream(archiveBytes);
            using var archive = new ZipArchive(memory, ZipArchiveMode.Read);
            var entry = archive.Entries.FirstOrDefault(e =>
                Path.GetFileName(e.FullName).Equals("retoc.exe", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException("retoc.exe is missing from the release archive");

            var temporary = executable + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var source = entry.Open())
                await using (var target = File.Create(temporary))
                    await source.CopyToAsync(target, ct);

                if (Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(temporary, ct)))
                    != ExecutableSha256)
                    throw new InvalidDataException("retoc executable failed its SHA-256 check");

                File.Move(temporary, executable, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }

            return executable;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new UserFacingException(
                $"Could not obtain retoc: {e.Message}",
                "Download retoc yourself and pass --retoc <file>.");
        }
    }

    public static async Task ConvertAsync(string executable, Options options, CancellationToken ct)
    {
        // Cli.Prepare rejects several keys up front; a second key here means a caller skipped it.
        if (options.AesKeys.Count > 1)
            throw new UserFacingException("retoc accepts only one AES key for a conversion run.");

        var start = new ProcessStartInfo(executable)
        {
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        };

        if (options.AesKeys.Count == 1)
        {
            var (_, key) = Cli.SplitAesKey(options.AesKeys[0]);
            start.ArgumentList.Add("--aes-key");
            start.ArgumentList.Add(key);
        }

        start.ArgumentList.Add("to-legacy");

        // retoc can usually infer this from the container header, but some games use a header
        // version shared by several engine releases. Honour UEBulkExport's --game option so the
        // conversion is deterministic instead of silently relying on that inference.
        var engineVersion = ToRetocEngineVersion(options.Game.ToString());
        if (engineVersion is not null)
        {
            start.ArgumentList.Add("--version");
            start.ArgumentList.Add(engineVersion);
            Log.Info($"retoc engine version: {engineVersion}");
        }

        start.ArgumentList.Add(options.PaksDirectory);
        start.ArgumentList.Add(options.OutputDirectory);

        Process? started;
        try { started = Process.Start(start); }
        catch (Win32Exception e)
        {
            throw new UserFacingException($"Could not start retoc: {e.Message}",
                "Check the --retoc executable and its platform.");
        }

        using var process = started
            ?? throw new UserFacingException("Could not start retoc.", "Check the --retoc executable path.");

        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            var output = await stdout;
            var errors = await stderr;

            if (process.ExitCode != 0)
                throw new UserFacingException(
                    $"retoc conversion failed (exit {process.ExitCode}).",
                    LastLines(string.IsNullOrWhiteSpace(errors) ? output : errors));

            foreach (var line in output.Split('\n').Where(l => l.Contains("Extracted", StringComparison.OrdinalIgnoreCase)))
                Log.Info($"retoc: {line.Trim()}");
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }
    }

    private static string LastLines(string output) =>
        string.Join(Environment.NewLine, output.Split('\n').TakeLast(8)).Trim();

    internal static string? ToRetocEngineVersion(string cue4ParseGame)
    {
        const string prefix = "GAME_UE";
        if (!cue4ParseGame.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;

        var components = cue4ParseGame[prefix.Length..].Split('_');
        if (components.Length != 2 ||
            !int.TryParse(components[0], out var major) ||
            !int.TryParse(components[1], out var minor))
            return null;

        return $"UE{major}_{minor}";
    }
}
