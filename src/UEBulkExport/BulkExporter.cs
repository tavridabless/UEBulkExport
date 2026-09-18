using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.MappingsProvider.Usmap;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.Sound;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Assets.Exports.Wwise;
using CUE4Parse.UE4.IO.Objects;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.Engine.Animation;
using CUE4Parse.UE4.Versions;
using CUE4Parse_Conversion;
using CUE4Parse_Conversion.Exporters;
using CUE4Parse_Conversion.Options;
using CUE4Parse_Conversion.Sounds;

namespace UEBulkExport;

public sealed class BulkExporter : IDisposable
{
    private readonly Options _options;
    private readonly ExportOptions _exportOptions;
    private readonly ExportOptions _animExportOptions;
    private readonly ExportOptions _worldExportOptions;
    private readonly DefaultFileProvider _provider;
    private readonly ConcurrentBag<ExportError> _errors = [];
    private readonly HashSet<string> _alreadyDone = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _indexGate = new();
    private StreamWriter? _index;
    private bool _indexLoaded;

    private int _processed, _exported, _skipped, _failed, _written, _unsupported, _retocPackages;

    private readonly record struct ExportError(string Path, string Exception, string Message);

    public BulkExporter(Options options)
    {
        _options = options;
        _exportOptions = options.ToExportOptions();
        _animExportOptions = options.ToAnimExportOptions();
        _worldExportOptions = options.ToWorldExportOptions();

        _provider = new DefaultFileProvider(
            options.PaksDirectory,
            SearchOption.AllDirectories,
            new VersionContainer(options.Game, options.Platform));
    }

    // ------------------------------------------------------------------ mounting

    public void Mount()
    {
        _provider.Initialize();
        SubmitKeys();
        _provider.Mount();

        Log.Info($"mounted {_provider.MountedVfs.Count} container(s), {_provider.Files.Count} entries");
        foreach (var vfs in _provider.MountedVfs)
            Log.Info($"  {vfs.Name}: {vfs.FileCount} entries");

        if (_provider.Files.Count == 0)
            throw new UserFacingException(
                $"No readable entries in {_options.PaksDirectory}.",
                "If the containers are encrypted, supply the key with --aes 0x<64 hex chars>.");

        ReportLockedContainers();

        if (_options.UsmapPath is not null)
        {
            _provider.MappingsContainer = new FileUsmapTypeMappingsProvider(_options.UsmapPath);
            Log.Info($"mappings: {_options.UsmapPath}");
        }

        TryQuietly(() => _provider.LoadLocalization());
        TryQuietly(() => _provider.LoadVirtualPaths());
    }

    private void ReportLockedContainers()
    {
        // global.utoc carries shared script and name data rather than assets; the provider folds
        // it into GlobalData instead of mounting it, so it is expected to look "unloaded".
        var locked = _provider.UnloadedVfs
            .Where(vfs => !vfs.Name.StartsWith("global.", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (locked.Count == 0) return;

        Log.Warn($"{locked.Count} container(s) stayed locked - wrong or missing AES key:");
        foreach (var vfs in locked)
            Log.Warn($"  {vfs.Name} (key guid {vfs.EncryptionKeyGuid})");
    }

    private void SubmitKeys()
    {
        var keys = new List<KeyValuePair<FGuid, FAesKey>>
        {
            // The zero GUID covers unencrypted containers and those left on the default key.
            new(new FGuid(), new FAesKey(new byte[32]))
        };

        foreach (var raw in _options.AesKeys)
        {
            var separator = raw.LastIndexOf(':');

            if (separator > 0 && !raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                keys.Add(new KeyValuePair<FGuid, FAesKey>(new FGuid(raw[..separator]), new FAesKey(raw[(separator + 1)..])));
            else
                keys.Add(new KeyValuePair<FGuid, FAesKey>(new FGuid(), new FAesKey(raw)));
        }

        _provider.SubmitKeys(keys);
    }

    /// <summary>
    /// Shipping UE5 packages store properties unversioned, so without a mappings file nothing at
    /// all can be deserialized. Find that out on one package up front rather than after ten
    /// thousand identical failures.
    /// </summary>
    public void VerifyMappings(IReadOnlyList<GameFile> files)
    {
        if (!_options.RequiresMappings) return;

        var probe = files.FirstOrDefault(f => f.IsUePackage);
        if (probe is null) return;

        try
        {
            _provider.LoadPackage(probe);
        }
        catch (Exception e) when (e.Message.Contains("mapping", StringComparison.OrdinalIgnoreCase))
        {
            throw new UserFacingException(
                "This build stores properties unversioned and cannot be read without a mappings file.",
                """
                  Every shipping Unreal Engine 5 build is like this, and no tool can work around it:
                  the packages hold hashes where the property names should be.

                  Two ways forward:

                    1. Supply a .usmap and run again:
                         UEBulkExport ... --usmap "path\to\Mappings.usmap"
                       Drop a single .usmap next to this executable and it is picked up on its own.
                       See the README for how to generate one from the running game with UE4SS.

                    2. Take a byte-exact dump instead, which needs no mappings:
                         UEBulkExport ... --mode raw
                """);
        }
    }

    // ------------------------------------------------------------------ selection

    public IReadOnlyList<GameFile> SelectFiles()
    {
        var include = CompileFilter(_options.IncludeRegex, "--include");
        var exclude = CompileFilter(_options.ExcludeRegex, "--exclude");

        var selected = _provider.Files.Values
            .DistinctBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .Where(f => _options.Mode != ExportMode.Legacy || f.IsUePackage)
            .Where(f => include is null || include.IsMatch(f.Path))
            .Where(f => exclude is null || !exclude.IsMatch(f.Path))
            .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (selected.Count == 0 && (include is not null || exclude is not null))
            throw new UserFacingException(
                "The filters matched nothing.",
                "Check --include / --exclude, or run with --mode list to see the container paths.");

        if (selected.Count == 0 && _options.Mode == ExportMode.Legacy)
            throw new UserFacingException("No .uasset or .umap packages were found.",
                "Run --mode list to inspect the container, or use --mode raw for loose files.");

        return selected;
    }

    private static Regex? CompileFilter(string? pattern, string argument)
    {
        if (pattern is null) return null;

        try { return new Regex(pattern, RegexOptions.IgnoreCase); }
        catch (ArgumentException e)
        {
            throw new UserFacingException($"{argument} is not a valid regular expression: {e.Message}");
        }
    }

    public void PrintListing(IReadOnlyList<GameFile> files)
    {
        Log.Raw("");
        Log.Raw($"{"extension",-24}{"count",10}{"size, MB",14}");
        Log.Raw(new string('-', 48));

        foreach (var group in files.GroupBy(f => f.Extension.ToLowerInvariant()).OrderByDescending(g => g.Sum(f => f.Size)))
            Log.Raw($"{"." + group.Key,-24}{group.Count(),10}{group.Sum(f => f.Size) / 1048576.0,14:N1}");

        Log.Raw(new string('-', 48));
        Log.Raw($"{"TOTAL",-24}{files.Count,10}{files.Sum(f => f.Size) / 1048576.0,14:N1}");

        Log.Raw("");
        Log.Raw("top level folders:");
        foreach (var group in files.GroupBy(f => f.Path.Split('/')[0]).OrderByDescending(g => g.Count()))
            Log.Raw($"  {group.Key,-32}{group.Count(),8} entries");
    }

    /// <summary>Shows how each entry would be treated, without touching the disk.</summary>
    public void PrintPlan(IReadOnlyList<GameFile> files)
    {
        LoadIndex();
        var work = files.Where(ShouldProcess).ToList();

        var packages = work.Count(f => f.IsUePackage);
        var loose = work.Count(f => !f.IsUePackage && !f.IsUePackagePayload);
        var payloads = work.Count(f => f.IsUePackagePayload);

        Log.Raw("");
        Log.Raw("dry run - nothing will be written");
        Log.Raw("");
        Log.Raw($"  mode                 {_options.Mode.ToString().ToLowerInvariant()}");
        Log.Raw($"  output               {Path.GetFullPath(_options.OutputDirectory)}");
        Log.Raw($"  mappings             {_options.UsmapPath ?? "(none)"}");
        Log.Raw("");
        Log.Raw($"  entries selected     {files.Count}");
        Log.Raw($"  already done         {files.Count - work.Count}");
        if (_options.Mode == ExportMode.Legacy)
        {
            Log.Raw($"  packages to extract  {packages}");
            Log.Raw("  payloads             included with their packages");
            Log.Raw($"  IoStore conversion   {work.Count(f => f.IsUePackage && f is FIoStoreEntry)} package(s) via retoc");
            Log.Raw("  editor compatibility cooked assets only; supported types may load read-only");
        }
        else
        {
            Log.Raw($"  packages to {(_options.Mode == ExportMode.Raw ? "copy" : "parse"),-8} {packages}");
            Log.Raw($"  loose files to copy  {loose}");
            Log.Raw($"  payload entries      {payloads}");
        }

        if (_options.Mode is ExportMode.Full)
        {
            Log.Raw("");
            Log.Raw($"  meshes as            {_options.MeshFormat}");
            Log.Raw($"  animations as        {_options.AnimFormat}");
            Log.Raw($"  textures as          {_options.TextureFormat}");
            Log.Raw($"  levels               {(_options.ExportWorlds ? "USD" : "skipped")}");
        }
    }

    // ------------------------------------------------------------------ driving

    public async Task<int> RunAsync(IReadOnlyList<GameFile> files, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_options.OutputDirectory);
        OpenIndex();

        var work = files.Where(ShouldProcess).ToList();
        var preSkipped = files.Count - work.Count;
        Log.Info($"{work.Count} entries to process ({preSkipped} skipped by mode/resume), {_options.Threads} threads");

        var clock = Stopwatch.StartNew();
        await using var progress = StartProgressReporter(work.Count, clock);

        if (_options.Mode == ExportMode.Legacy)
            await ConvertIoStoreAsync(work, ct);

        var individualWork = _options.Mode == ExportMode.Legacy
            ? work.Where(f => f is not FIoStoreEntry).ToList()
            : work;

        await Parallel.ForEachAsync(individualWork,
            new ParallelOptions { MaxDegreeOfParallelism = _options.Threads, CancellationToken = ct },
            async (file, token) =>
            {
                try
                {
                    if (await ProcessFileAsync(file, token)) Interlocked.Increment(ref _exported);
                    else Interlocked.Increment(ref _skipped);

                    MarkDone(file.Path);
                }
                catch (Exception e)
                {
                    Interlocked.Increment(ref _failed);
                    RecordError(file.Path, e);
                }
                finally
                {
                    Interlocked.Increment(ref _processed);
                }
            });

        clock.Stop();
        CloseIndex();
        WriteErrorReport();
        PrintSummary(clock.Elapsed, preSkipped);

        return _failed == 0 ? 0 : 2;
    }

    private async Task ConvertIoStoreAsync(IReadOnlyList<GameFile> work, CancellationToken ct)
    {
        var packages = work.Where(f => f.IsUePackage && f is FIoStoreEntry).ToList();
        if (packages.Count == 0) return;

        // retoc's filter is a substring, not a regex. Applying our regex after conversion would
        // require writing potentially the entire game to a temporary directory first.
        if (_options.IncludeRegex is not null || _options.ExcludeRegex is not null)
            throw new UserFacingException(
                "--include/--exclude cannot be used with legacy IoStore conversion.",
                "retoc cannot apply regular-expression filters. Use --mode raw for filtered byte dumps.");

        // A previous raw run may already have written a Zen .uasset at the same path. Require
        // retoc to actually create or replace every package before marking it converted.
        var previousWrites = packages.ToDictionary(f => f.Path, f =>
        {
            var path = OutputPath(f.Path);
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : (DateTime?) null;
        }, StringComparer.OrdinalIgnoreCase);

        var executable = await Retoc.ResolveAsync(_options.RetocPath, ct);
        Log.Info($"converting {packages.Count} IoStore package(s) to legacy cooked format with retoc");
        await Retoc.ConvertAsync(executable, _options, ct);

        foreach (var file in packages)
        {
            var path = OutputPath(file.Path);
            if (File.Exists(path) && (previousWrites[file.Path] is null ||
                                      File.GetLastWriteTimeUtc(path) > previousWrites[file.Path]))
            {
                _retocPackages++;
                _exported++;
                MarkDone(file.Path);
            }
            else
            {
                _failed++;
                RecordError(file.Path, new IOException(
                    "retoc did not create or replace the expected package; try an empty --out directory"));
            }

            _processed++;
        }
    }

    private void PrintSummary(TimeSpan elapsed, int preSkipped)
    {
        Log.Raw("");
        Log.Info($"done in {elapsed:hh\\:mm\\:ss}");
        Log.Info($"  entries processed : {_processed}");
        Log.Info($"  entries exported  : {_exported}");
        Log.Info($"  files written     : {_written}" +
                 (_retocPackages > 0 ? " (plus files written by retoc)" : ""));
        if (_retocPackages > 0)
            Log.Info($"  IoStore converted : {_retocPackages}");
        Log.Info($"  nothing to do     : {_skipped + preSkipped}");
        if (_options.Mode is ExportMode.Full)
            Log.Info($"  no converter      : {_unsupported}  (properties still written as .json)");

        if (_failed > 0)
            Log.Warn($"  failed            : {_failed}  (see errors.csv)");

        Log.Info($"  output            : {Path.GetFullPath(_options.OutputDirectory)}");
    }

    private bool ShouldProcess(GameFile file)
    {
        if (_options.Resume && _alreadyDone.Contains(file.Path)) return false;
        if (_options.Mode == ExportMode.Raw) return true;
        if (_options.Mode == ExportMode.Legacy) return file.IsUePackage;
        if (file.IsUePackagePayload) return _options.WriteRawPackages;
        if (file.IsUePackage) return true;

        return _options.Mode == ExportMode.Full && _options.WriteRawMisc;
    }

    // ------------------------------------------------------------------ per entry

    private async Task<bool> ProcessFileAsync(GameFile file, CancellationToken ct)
    {
        if (_options.Mode == ExportMode.Raw)
            return WriteBytes(OutputPath(file.Path), file.Read());

        if (_options.Mode == ExportMode.Legacy)
        {
            var wrotePackage = false;
            foreach (var (path, bytes) in _provider.SavePackage(file))
                wrotePackage |= WriteBytes(OutputPath(path), bytes);
            return wrotePackage;
        }

        if (!file.IsUePackage)
        {
            if (file.IsUePackagePayload && !_options.WriteRawPackages) return false;
            return _options.WriteRawMisc && WriteBytes(OutputPath(file.Path), file.Read());
        }

        var wrote = false;

        if (_options.WriteRawPackages)
            foreach (var (path, bytes) in _provider.SavePackage(file))
                wrote |= WriteBytes(OutputPath(path), bytes);

        var package = _provider.LoadPackage(file);
        var exports = package.GetExports().ToArray();

        // The property dump and the asset conversion have to be separate passes: a session keys
        // its queue by object path, so queuing both for one object silently drops one of them.
        if (_options.WriteJson)
            wrote |= await RunSessionAsync(session =>
            {
                foreach (var export in exports)
                {
                    // With --materials the material exporter writes its own <name>.json and would
                    // overwrite this one, so leave materials to it.
                    if (_options.ExportMaterials && export is UMaterialInterface) continue;
                    session.Add(new JsonPropertiesExporter(export));
                }
            }, file.Path, _exportOptions, ct);

        if (_options.WriteAssets && _options.Mode == ExportMode.Full)
        {
            // Animations, bare skeletons and levels each need a format glTF cannot provide,
            // so every group runs as its own pass with its own options.
            wrote |= await RunSessionAsync(s => Queue(s, exports, ExportKind.Standard), file.Path, _exportOptions, ct);
            wrote |= await RunSessionAsync(s => Queue(s, exports, ExportKind.Animation), file.Path, _animExportOptions, ct);

            if (_options.ExportWorlds)
                wrote |= await RunSessionAsync(s => Queue(s, exports, ExportKind.World), file.Path, _worldExportOptions, ct);

            wrote |= await ExportSoundsAsync(exports, file, ct);
        }

        return wrote;
    }

    private enum ExportKind { Standard, Animation, World }

    private void Queue(ExportSession session, UObject[] exports, ExportKind kind)
    {
        foreach (var export in exports)
        {
            if (KindOf(export) != kind) continue;
            if (!_options.ExportMaterials && export is UMaterialInterface) continue;
            if (HasNothingToConvert(export)) continue;

            // Plenty of export types have no file form at all: components, user data, notifies.
            // The session says so by throwing; their properties are covered by the .json pass.
            try { session.Add(export); }
            catch (NotSupportedException) { Interlocked.Increment(ref _unsupported); }
        }
    }

    private static ExportKind KindOf(UObject export) => export switch
    {
        UWorld => ExportKind.World,
        UAnimationAsset or USkeleton => ExportKind.Animation,
        _ => ExportKind.Standard
    };

    /// <summary>
    /// Some assets exist only at runtime or describe a graph rather than data: render targets and
    /// media textures are filled by the engine and hold no pixels on disk, and a blend space is a
    /// set of rules for mixing other animations. Their properties still land in the .json pass.
    /// </summary>
    private static bool HasNothingToConvert(UObject export) =>
        export is UTextureRenderTarget or UMediaTexture or UBinkMediaTexture or UBlendSpaceBase;

    /// <summary>
    /// Runs one export session rooted at the output folder. CUE4Parse rebuilds the container path
    /// from each object's package, so the result mirrors the container tree without further work.
    /// </summary>
    private async Task<bool> RunSessionAsync(Action<ExportSession> fill, string containerPath,
        ExportOptions options, CancellationToken ct)
    {
        var session = new ExportSession { MaxDegreeOfParallelism = 1 };
        fill(session);

        if (!session.HasQueuedItems) return false;

        var results = await session.RunAsync(_options.OutputDirectory, options, ct: ct);
        var wrote = false;

        foreach (var result in results)
        {
            if (!result.Success)
            {
                // "No converter for this type" is a statement about the library, not a problem
                // with the asset, so keep it out of the error report.
                if (result.Error is NotSupportedException)
                {
                    Interlocked.Increment(ref _unsupported);
                    if (_options.Verbose) Log.Raw($"  - {result.ObjectPath}: {result.Error.Message}");
                    continue;
                }

                Interlocked.Increment(ref _failed);
                RecordError($"{containerPath} :: {result.ObjectPath}",
                    result.Error ?? new Exception("exporter reported failure"));
                continue;
            }

            foreach (var path in result.DiskFilePaths ?? [])
            {
                Interlocked.Increment(ref _written);
                wrote = true;
                if (_options.Verbose) Log.Raw($"  + {path}");
            }
        }

        return wrote;
    }

    // ------------------------------------------------------------------ audio

    /// <summary>
    /// Sound waves are the one asset class CUE4Parse has no exporter for, so decode them here.
    /// Shipping audio is usually Bink, which almost nothing reads, so hand it to vgmstream for a
    /// .wav as well when that tool is around.
    /// </summary>
    private async Task<bool> ExportSoundsAsync(UObject[] exports, GameFile file, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(OutputPath(file.Path))!;
        var wrote = false;

        foreach (var export in exports)
        {
            if (export is not (USoundWave or UAkMediaAssetData)) continue;

            SoundDecoder.Decode(export, true, out var format, out var data);
            if (data is null || data.Length == 0) continue;

            var extension = string.IsNullOrWhiteSpace(format) ? "bin" : format.ToLowerInvariant().TrimStart('.');
            var basePath = Path.Combine(directory, SafeName(export.Name));
            var soundPath = $"{basePath}.{extension}";

            wrote |= WriteBytes(soundPath, data);

            if (_options.ConvertAudio && extension is not ("wav" or "ogg"))
                wrote |= await Audio.TryConvertToWavAsync(soundPath, $"{basePath}.wav", _options.Resume, ct);
        }

        return wrote;
    }

    // ------------------------------------------------------------------ output plumbing

    private string OutputPath(string containerPath) =>
        Path.Combine(_options.OutputDirectory, containerPath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Object names come from the asset and can carry characters the filesystem rejects.</summary>
    private static string SafeName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name;
    }

    private bool WriteBytes(string path, byte[] data)
    {
        if (_options.Resume && File.Exists(path)) return false;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data);
        Interlocked.Increment(ref _written);

        if (_options.Verbose) Log.Raw($"  + {path}");
        return true;
    }

    // ------------------------------------------------------------------ resume index

    // A previous JSON or conversion run must not mark packages as extracted.
    private string IndexPath => Path.Combine(_options.OutputDirectory,
        $"_completed.{_options.Mode.ToString().ToLowerInvariant()}.txt");

    private void OpenIndex()
    {
        if (File.Exists(IndexPath))
        {
            if (_options.Resume)
            {
                LoadIndex();
            }
            else
            {
                File.Delete(IndexPath);
            }
        }

        _index = new StreamWriter(IndexPath, append: true) { AutoFlush = false };
    }

    private void LoadIndex()
    {
        if (_indexLoaded || !_options.Resume) return;
        _indexLoaded = true;
        if (!File.Exists(IndexPath)) return;

        foreach (var line in File.ReadLines(IndexPath))
            if (line.Length > 0) _alreadyDone.Add(line);

        Log.Info($"resuming: {_alreadyDone.Count} entries already finished (pass --overwrite to redo them)");
    }

    private void MarkDone(string containerPath)
    {
        lock (_indexGate)
        {
            _index?.WriteLine(containerPath);
            if (_processed % 256 == 0) _index?.Flush();
        }
    }

    private void CloseIndex()
    {
        lock (_indexGate)
        {
            _index?.Flush();
            _index?.Dispose();
            _index = null;
        }
    }

    // ------------------------------------------------------------------ diagnostics

    private void RecordError(string path, Exception e)
    {
        var message = e.Message.Split('\n')[0].Replace('"', '\'').Trim();
        _errors.Add(new ExportError(path, e.GetType().Name, message));

        if (_options.Verbose) Log.Error($"{path}: {e.GetType().Name}: {message}");
    }

    private void WriteErrorReport()
    {
        if (_errors.IsEmpty) return;

        var path = Path.Combine(_options.OutputDirectory, "errors.csv");

        using (var writer = new StreamWriter(path, append: false))
        {
            writer.WriteLine("path,exception,message");
            foreach (var error in _errors.OrderBy(e => e.Path, StringComparer.Ordinal))
                writer.WriteLine($"\"{error.Path}\",\"{error.Exception}\",\"{error.Message}\"");
        }

        Log.Raw("");
        Log.Warn("most common failures:");

        foreach (var group in _errors.GroupBy(e => $"{e.Exception}: {e.Message}")
                     .OrderByDescending(g => g.Count())
                     .Take(5))
            Log.Warn($"  [{group.Count(),6}] {group.Key}");
    }

    private Timer StartProgressReporter(int total, Stopwatch clock)
    {
        return new Timer(_ =>
        {
            var done = Volatile.Read(ref _processed);
            if (done == 0 || total == 0) return;

            var rate = done / Math.Max(1.0, clock.Elapsed.TotalSeconds);
            var eta = TimeSpan.FromSeconds((total - done) / Math.Max(0.001, rate));

            Log.Raw($"  {done}/{total} ({done * 100.0 / total:F1}%)  {rate:F0}/s  " +
                    $"written {Volatile.Read(ref _written)}  failed {Volatile.Read(ref _failed)}  eta {eta:hh\\:mm\\:ss}");
        }, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    private static void TryQuietly(Action action)
    {
        try { action(); } catch { /* optional extras: localization tables, virtual paths */ }
    }

    public void Dispose()
    {
        CloseIndex();
        _provider.Dispose();
    }
}
