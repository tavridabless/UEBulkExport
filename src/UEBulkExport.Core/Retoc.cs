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
    /// <summary>The engine versions retoc 0.1.5 accepts for <c>--version</c>.</summary>
    internal static readonly HashSet<string> KnownEngineVersions = new(StringComparer.OrdinalIgnoreCase)
    {
        "UE4_25", "UE4_26", "UE4_27", "UE5_0", "UE5_1", "UE5_2", "UE5_3", "UE5_4", "UE5_5", "UE5_6", "UE5_7"
    };

    private const string DownloadUrl =
        "https://github.com/trumank/retoc/releases/download/v0.1.5/retoc_cli-x86_64-pc-windows-msvc.zip";
    private const string DownloadSha256 =
        "CC036B06AD3BDCF7003690B00D82719980C374E48A95BF0654F9959148D263AA";
    private const string ExecutableSha256 =
        "A145F3557EC60B163ED1B78B2F380232D2753FA30350C8013C98E171DB3AE7E7";

    public static async Task<string> ResolveAsync(string? explicitPath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (explicitPath is not null) return Path.GetFullPath(explicitPath);

        if (!OperatingSystem.IsWindows() || System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
            != System.Runtime.InteropServices.Architecture.X64)
            throw new UserFacingException(
                "IoStore legacy conversion needs retoc.",
                "Download retoc for this platform and pass its executable with --retoc <file>.");

        var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UEBulkExport", "native");
        var executable = Path.Combine(cache, "retoc-0.1.5.exe");
        try
        {
            if (File.Exists(executable) && await FileMatchesHashAsync(executable, ct)) return executable;

            Directory.CreateDirectory(cache);
            Log.Info("downloading retoc 0.1.5 for IoStore package conversion ...");
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

                if (!await FileMatchesHashAsync(temporary, ct))
                    throw new InvalidDataException("retoc executable failed its SHA-256 check");

                File.Move(temporary, executable, overwrite: true);
            }
            finally
            {
                AtomicFile.TryDelete(temporary);
            }

            return executable;
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            throw new UserFacingException(
                $"Could not obtain retoc: {e.Message}",
                "Download retoc yourself and pass --retoc <file>.");
        }
    }

    public static async Task ConvertAsync(string executable, Options options, string inputPath,
        string outputDirectory, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // Cli.Prepare rejects several keys up front; a second key here means a caller skipped it.
        if (options.AesKeys.Count > 1)
            throw new UserFacingException("retoc accepts only one AES key for a conversion run.");

        var start = new ProcessStartInfo(executable)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        };

        string? key = null;
        if (options.AesKeys.Count == 1)
        {
            (_, key) = Cli.SplitAesKey(options.AesKeys[0]);
            start.ArgumentList.Add("--aes-key");
            start.ArgumentList.Add(key);
        }

        start.ArgumentList.Add("to-legacy");

        // retoc can usually infer this from the container header, but some games use a header
        // version shared by several engine releases. Honour UEBulkExport's --game option so the
        // conversion is deterministic instead of silently relying on that inference.
        // A newer engine than retoc knows is left to that inference rather than rejected outright.
        var engineVersion = ToRetocEngineVersion(options.Game.ToString());
        var unknownVersion = engineVersion is not null && !KnownEngineVersions.Contains(engineVersion);
        if (unknownVersion)
            Log.Warn($"retoc 0.1.5 does not know {engineVersion}; it reads the version from the containers instead.");
        else if (engineVersion is not null)
        {
            start.ArgumentList.Add("--version");
            start.ArgumentList.Add(engineVersion);
            Log.Info($"retoc engine version: {engineVersion}");
        }

        start.ArgumentList.Add(inputPath);
        start.ArgumentList.Add(outputDirectory);

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
            var stdout = ReadOutputTailAsync(process.StandardOutput, key, logSummary: true, ct);
            var stderr = ReadOutputTailAsync(process.StandardError, key, logSummary: false, ct);
            await process.WaitForExitAsync(ct);

            var tails = await Task.WhenAll(stdout, stderr);
            var output = tails[0];
            var errors = tails[1];

            if (process.ExitCode != 0)
                throw new UserFacingException(
                    $"retoc conversion failed (exit {process.ExitCode}).",
                    (string.IsNullOrWhiteSpace(errors) ? output : errors) + (unknownVersion
                        ? Environment.NewLine + Environment.NewLine +
                          $"No explicit {engineVersion} argument was passed. retoc tried to infer the version " +
                          "from the containers. Try raw mode, converted files, or a newer build with --retoc."
                        : ""));
        }
        catch (OperationCanceledException)
        {
            Stop(process);
            throw;
        }
    }

    private static void Stop(Process process)
    {
        try
        {
            if (process.HasExited) return;
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
        }
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }
    }

    private static async Task<bool> FileMatchesHashAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)) == ExecutableSha256;
    }

    internal static async Task<string> ReadOutputTailAsync(TextReader reader, string? key, bool logSummary,
        CancellationToken ct)
    {
        const int tailSize = 8;
        var tail = new Queue<string>(tailSize);
        var secret = key is not null && key.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? key[2..] : key;

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!string.IsNullOrEmpty(secret))
                line = line.Replace(secret, "[redacted]", StringComparison.OrdinalIgnoreCase);

            if (logSummary && line.Contains("Extracted", StringComparison.OrdinalIgnoreCase))
                Log.Info($"retoc: {line.Trim()}");
            if (tail.Count == tailSize) tail.Dequeue();
            tail.Enqueue(line);
        }

        return string.Join(Environment.NewLine, tail).Trim();
    }

    /// <summary>
    /// Reports whether the bundled retoc accepts an explicit version for a plain engine profile.
    /// Game-specific profiles are always passed through for container-based detection.
    /// </summary>
    public static bool SupportsEngine(string cue4ParseGame) =>
        ToRetocEngineVersion(cue4ParseGame) is not { } version || KnownEngineVersions.Contains(version);

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
