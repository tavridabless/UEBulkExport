using System.Text;
using System.Text.RegularExpressions;

namespace UEBulkExport;

/// <summary>
/// Reads the engine version a packaged game was built with. Every build of the engine embeds its
/// branch name, such as <c>++UE5+Release-5.3</c>, in the game executable; that is far more
/// reliable than asking the user, who rarely knows. Detection is a best effort: custom engine
/// branches rename it, and then the caller falls back to a manual choice.
/// </summary>
public static class EngineDetection
{
    /// <summary>Launchers and crash reporters are small; the game itself is tens of megabytes.</summary>
    private const long MinimumGameExecutableSize = 8L * 1024 * 1024;

    private const int ChunkSize = 4 * 1024 * 1024;

    private static readonly Regex Ascii = new(@"\+\+UE(?<major>[45])\+Release-(?<version>\d+\.\d+)",
        RegexOptions.CultureInvariant);

    private static readonly byte[] AsciiMarker = "++UE"u8.ToArray();
    private static readonly byte[] Utf16Marker = Encoding.Unicode.GetBytes("++UE");

    /// <summary>
    /// Finds the game executable belonging to the containers and reads its engine version, as
    /// "5.3". Returns null when there is no executable or it carries no recognisable version.
    /// </summary>
    public static string? DetectFromContainers(string paksDirectory, CancellationToken ct = default)
    {
        foreach (var executable in FindGameExecutables(paksDirectory))
        {
            ct.ThrowIfCancellationRequested();
            var version = ReadVersion(executable, ct);
            if (version is not null) return version;
        }

        return null;
    }

    /// <summary>
    /// The containers live in <c>&lt;Game&gt;/Content/Paks</c>. A game with C++ code keeps its
    /// executable in <c>&lt;Game&gt;/Binaries/&lt;Platform&gt;</c>; a Blueprint-only game runs the
    /// stock one from <c>Engine/Binaries/&lt;Platform&gt;</c> next to it. Largest first: that is
    /// the game, not a helper.
    /// </summary>
    internal static IEnumerable<string> FindGameExecutables(string paksDirectory)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(paksDirectory));
        DirectoryInfo? content = directory;
        while (content is not null && !content.Name.Equals("Content", StringComparison.OrdinalIgnoreCase))
            content = content.Parent;

        if (content?.Parent is not { } game) return [];
        string[] binaries =
        [
            Path.Combine(game.FullName, "Binaries"),
            .. game.Parent is { } root ? [Path.Combine(root.FullName, "Engine", "Binaries")] : Array.Empty<string>()
        ];

        var found = new List<FileInfo>();
        foreach (var folder in binaries.Where(Directory.Exists))
        {
            try
            {
                found.AddRange(Directory.EnumerateDirectories(folder)
                    .SelectMany(platform => Directory.EnumerateFiles(platform, "*.exe", SearchOption.TopDirectoryOnly))
                    .Select(path => new FileInfo(path))
                    .Where(file => file.Length >= MinimumGameExecutableSize));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Unreadable; the other folder may still answer.
            }
        }

        // The game's own binaries first, then by size.
        return found
            .OrderByDescending(file => file.FullName.StartsWith(binaries[0], StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(file => file.Length)
            .Select(file => file.FullName)
            .ToList();
    }

    /// <summary>Scans a binary for the engine branch name, stored as ASCII or UTF-16.</summary>
    internal static string? ReadVersion(string executable, CancellationToken ct = default)
    {
        try
        {
            using var stream = new FileStream(executable, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                bufferSize: 1, FileOptions.SequentialScan);

            // Chunks overlap by a little more than the longest match so one cannot be split in two.
            const int overlap = 128;
            var buffer = new byte[ChunkSize + overlap];
            var carried = 0;
            int read;
            while ((read = stream.Read(buffer, carried, ChunkSize)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                var length = carried + read;
                var span = buffer.AsSpan(0, length);

                if (Find(span, AsciiMarker) is { } version) return version;
                if (Find(span, Utf16Marker, utf16: true) is { } wide) return wide;

                carried = Math.Min(overlap, length);
                Array.Copy(buffer, length - carried, buffer, 0, carried);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A locked or unreadable file simply has no answer.
        }

        return null;
    }

    private static string? Find(ReadOnlySpan<byte> data, byte[] marker, bool utf16 = false)
    {
        var offset = 0;
        while (offset < data.Length)
        {
            var index = data[offset..].IndexOf(marker);
            if (index < 0) return null;

            var start = offset + index;
            var window = data.Slice(start, Math.Min(utf16 ? 64 : 32, data.Length - start));
            var text = utf16
                ? Encoding.Unicode.GetString(window[..(window.Length & ~1)])
                : Encoding.ASCII.GetString(window);
            var match = Ascii.Match(text);
            if (match.Success && match.Index == 0 && match.Groups["version"].Value.StartsWith(
                    match.Groups["major"].Value + ".", StringComparison.Ordinal))
                return match.Groups["version"].Value;

            offset = start + 1;
        }

        return null;
    }
}
