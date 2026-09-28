using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UEBulkExport;

/// <summary>
/// Durable per-entry checkpoints. A record is trusted only for the same source/options profile
/// and while every file it published still exists with the recorded length.
/// </summary>
internal sealed class ResumeJournal : IDisposable
{
    private sealed record Artifact(string Path, long Length);
    private sealed record Entry(string Profile, string Path, IReadOnlyList<Artifact> Outputs);

    private readonly string _outputDirectory;
    private readonly string _profile;
    private readonly FileStream _stream;
    private readonly StreamWriter _writer;
    private readonly Lock _gate = new();

    public ResumeJournal(string path, string outputDirectory, string profile, bool overwrite)
    {
        _outputDirectory = Path.GetFullPath(outputDirectory);
        _profile = profile;

        if (overwrite && File.Exists(path)) File.Delete(path);

        var tornTail = EndsWithoutNewline(path);
        _stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read,
            bufferSize: 4096, FileOptions.WriteThrough);
        _writer = new StreamWriter(_stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        // A line cut by a power loss must not swallow the first record of this run.
        if (tornTail) _writer.WriteLine();
    }

    private static bool EndsWithoutNewline(string path)
    {
        if (!File.Exists(path)) return false;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (stream.Length == 0) return false;
        stream.Seek(-1, SeekOrigin.End);
        return stream.ReadByte() != '\n';
    }

    public static HashSet<string> Load(string path, string outputDirectory, string profile)
    {
        var completed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return completed;

        var invalid = 0;
        var legacy = 0;

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            if (!line.StartsWith('{'))
            {
                // A v1 path-only checkpoint cannot prove that a power loss did not truncate one
                // of the package payloads. Redo it once and replace it with a verified record.
                legacy++;
                continue;
            }

            try
            {
                var entry = JsonSerializer.Deserialize<Entry>(line);
                if (entry is null || entry.Path is null || entry.Outputs is null || entry.Profile != profile ||
                    !OutputsAreIntact(outputDirectory, entry.Outputs))
                {
                    invalid++;
                    continue;
                }

                completed.Add(entry.Path);
            }
            catch (JsonException)
            {
                // An abrupt shutdown can cut only the final JSON line. It is safe to redo it.
                invalid++;
            }
        }

        if (legacy > 0)
            Log.Warn($"resume index will recheck {legacy} legacy checkpoint(s) without output validation");
        if (invalid > 0)
            Log.Warn($"resume index ignored {invalid} stale or incomplete checkpoint(s)");

        return completed;
    }

    public void MarkDone(string containerPath, IEnumerable<string> outputPaths)
    {
        var requested = outputPaths
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var missing = requested.FirstOrDefault(path => !File.Exists(path));
        if (missing is not null)
            throw new IOException($"Cannot checkpoint a missing output file: {missing}");

        var outputs = requested
            .Select(path => new Artifact(RelativePath(_outputDirectory, path), new FileInfo(path).Length))
            .OrderBy(a => a.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var line = JsonSerializer.Serialize(new Entry(_profile, containerPath, outputs));

        lock (_gate)
        {
            _writer.WriteLine(line);
            _writer.Flush();
            _stream.Flush(flushToDisk: true);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer.Flush();
            _stream.Flush(flushToDisk: true);
            _writer.Dispose();
        }
    }

    public static string CreateProfile(Options options)
    {
        var lines = new List<string>
        {
            $"mode={options.Mode}", $"game={options.Game}", $"platform={options.Platform}",
            $"json={options.WriteJson}", $"assets={options.WriteAssets}",
            $"misc={options.WriteRawMisc}", $"rawPackages={options.WriteRawPackages}",
            $"materials={options.ExportMaterials}", $"audio={options.ConvertAudio}",
            $"worlds={options.ExportWorlds}", $"mesh={options.MeshFormat}",
            $"anim={options.AnimFormat}", $"nanite={options.NaniteMeshFormat}",
            $"quality={options.MeshQuality}", $"texture={options.TextureFormat}",
            $"sockets={options.SocketFormat}", $"morphs={options.ExportMorphTargets}",
            $"mips={options.ExportAllTextureMips}"
        };

        foreach (var path in ContainerFiles(options.PaksDirectory))
        {
            var info = new FileInfo(path);
            lines.Add($"container={Path.GetRelativePath(options.PaksDirectory, path)}|{info.Length}|{info.LastWriteTimeUtc.Ticks}");
        }

        if (options.UsmapPath is { } mappings && File.Exists(mappings))
        {
            var info = new FileInfo(mappings);
            lines.Add($"mappings={Path.GetFullPath(mappings)}|{info.Length}|{info.LastWriteTimeUtc.Ticks}");
        }

        var bytes = Encoding.UTF8.GetBytes(string.Join('\n', lines));
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static IEnumerable<string> ContainerFiles(string directory) =>
        Directory.EnumerateFiles(directory, "*",
                new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 })
            .Where(path => IsContainerExtension(Path.GetExtension(path)))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);

    private static bool IsContainerExtension(string extension) =>
        extension.Equals(".pak", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".utoc", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".ucas", StringComparison.OrdinalIgnoreCase);

    private static bool OutputsAreIntact(string outputDirectory, IEnumerable<Artifact> outputs)
    {
        foreach (var output in outputs)
        {
            var path = Path.GetFullPath(Path.Combine(outputDirectory,
                output.Path.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsInside(outputDirectory, path) || !File.Exists(path) || new FileInfo(path).Length != output.Length)
                return false;
        }

        return true;
    }

    private static string RelativePath(string root, string path)
    {
        if (!IsInside(root, path))
            throw new IOException($"Exported file escaped the output directory: {path}");

        return Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
    }

    private static bool IsInside(string root, string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar,
            StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }
}
