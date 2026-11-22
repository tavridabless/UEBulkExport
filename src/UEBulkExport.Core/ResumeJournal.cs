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
    private sealed record Checkpoint(string Line, TaskCompletionSource Completion);

    private readonly string _outputDirectory;
    private readonly string _profile;
    private readonly FileStream _stream;
    private readonly StreamWriter _writer;
    private readonly Lock _gate = new();
    private readonly Lock _pendingGate = new();
    private readonly List<Checkpoint> _pending = [];
    private bool _flushing;

    public ResumeJournal(string path, string outputDirectory, string profile, bool overwrite)
    {
        _outputDirectory = Path.GetFullPath(outputDirectory);
        _profile = profile;

        if (overwrite && File.Exists(path)) File.Delete(path);

        var tornTail = EndsWithoutNewline(path);
        _stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read,
            bufferSize: 4096, FileOptions.None);
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
                if (entry is null || entry.Path is null)
                {
                    invalid++;
                    continue;
                }

                // A later export of the same entry may have replaced files with same-sized
                // artifacts from a different profile. Only the latest checkpoint can confirm it.
                completed.Remove(entry.Path);
                if (entry.Outputs is null || entry.Profile != profile ||
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
        var line = CreateLine(containerPath, outputPaths);
        lock (_gate)
        {
            _writer.WriteLine(line);
            FlushToDisk();
        }
    }

    public Task MarkDoneAsync(string containerPath, IEnumerable<string> outputPaths)
    {
        var checkpoint = new Checkpoint(CreateLine(containerPath, outputPaths),
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        lock (_pendingGate)
        {
            _pending.Add(checkpoint);
            if (!_flushing)
            {
                _flushing = true;
                _ = Task.Run(FlushPending);
            }
        }
        return checkpoint.Completion.Task;
    }

    private void FlushPending()
    {
        while (true)
        {
            Checkpoint[] batch;
            lock (_pendingGate)
            {
                if (_pending.Count == 0)
                {
                    _flushing = false;
                    return;
                }
                batch = [.. _pending];
                _pending.Clear();
            }

            try
            {
                lock (_gate)
                {
                    foreach (var checkpoint in batch) _writer.WriteLine(checkpoint.Line);
                    FlushToDisk();
                }
                foreach (var checkpoint in batch) checkpoint.Completion.SetResult();
            }
            catch (Exception e)
            {
                foreach (var checkpoint in batch) checkpoint.Completion.SetException(e);
            }
        }
    }

    private string CreateLine(string containerPath, IEnumerable<string> outputPaths)
    {
        var requested = outputPaths
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var outputs = requested
            .Select(path =>
            {
                // FileInfo caches the same metadata for Exists and Length; avoid two separate
                // filesystem queries per artifact, including while loading a large resume index.
                var info = new FileInfo(path);
                if (!info.Exists)
                    throw new IOException($"Cannot checkpoint a missing output file: {path}");
                return new Artifact(RelativePath(_outputDirectory, path), info.Length);
            })
            .OrderBy(a => a.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return JsonSerializer.Serialize(new Entry(_profile, containerPath, outputs));
    }

    private void FlushToDisk()
    {
        // Each worker waits until its record is durable. Workers arriving during a disk flush
        // can share the next flush once their output files have been durably published.
        _writer.Flush();
        _stream.Flush(flushToDisk: true);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            FlushToDisk();
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

        // These options used to record selected payloads as finished without writing them.
        if (options.Mode is ExportMode.Full or ExportMode.Json && options.WriteRawPackages && !options.WriteRawMisc)
            lines.Add("rawPayloads=1");

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
            if (output?.Path is not { Length: > 0 } relative || Path.IsPathRooted(relative)) return false;
            try
            {
                var path = Path.GetFullPath(relative, Path.GetFullPath(outputDirectory));
                if (!IsInside(outputDirectory, path)) return false;
                var info = new FileInfo(path);
                if (!info.Exists || info.Length != output.Length) return false;
            }
            catch (ArgumentException)
            {
                return false;
            }
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
        return relative is not ("." or "..") && !relative.StartsWith(".." + Path.DirectorySeparatorChar,
            StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }
}
