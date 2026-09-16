using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using CUE4Parse.Compression;
using CUE4Parse_Conversion.Textures.BC;

namespace UEBulkExport;

/// <summary>
/// Everything that is not managed code. IoStore chunks are compressed with Oodle and zlib-ng,
/// animations are compressed with ACL, and a handful of texture formats need Detex. None of it
/// ships inside the NuGet packages, so each one is located, unpacked or downloaded here.
/// </summary>
public static class Natives
{
    /// <summary>The P/Invoke name CUE4Parse uses for its ACL and texture helper library.</summary>
    private const string NativesLibrary = "CUE4Parse-Natives";

    /// <summary>
    /// Last CUE4Parse package that shipped the native library. Its build still matches the
    /// current managed ABI, and the package is on nuget.org under Apache-2.0, which makes it a
    /// dependable place to fetch the library from without vendoring a binary.
    /// </summary>
    private const string NativesPackageUrl =
        "https://api.nuget.org/v3-flatcontainer/cue4parse/1.2.2/cue4parse.1.2.2.nupkg";

    private const string NativesEntryInPackage = "lib/net8.0/CUE4Parse-Natives.dll";

    private static string? _nativesPath;
    private static bool _resolverInstalled;

    private static readonly string[] OodleNames =
    [
        OodleHelper.OodleFileName,
        OodleHelper.OODLE_NAME_CURRENT, // oodle-data-shared.dll
        OodleHelper.OODLE_NAME_OLD,     // oo2core_9_win64.dll
        "oo2core_8_win64.dll"
    ];

    public static void Initialize(Options options)
    {
        var searchDirs = CandidateDirectories(options).ToList();

        SetUpZlib(options.ZlibPath, searchDirs);
        SetUpOodle(options.OodlePath, searchDirs);
        SetUpDetex(searchDirs);
        SetUpCUE4ParseNatives(searchDirs, options);
        Audio.Initialize(options, searchDirs);
    }

    /// <summary>Places a previous run, or an FModel install, tends to leave these libraries.</summary>
    public static IEnumerable<string> CandidateDirectories(Options options)
    {
        yield return AppContext.BaseDirectory;
        yield return Path.Combine(AppContext.BaseDirectory, ".data");
        yield return WritableCacheDirectory;
        yield return Directory.GetCurrentDirectory();
        yield return Path.Combine(Directory.GetCurrentDirectory(), ".data");

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(local, "FModel", ".data");

        // Walk up from the output folder: portable tools keep their helpers in a sibling .data
        // folder, and people usually export somewhere near them.
        var cursor = options.OutputDirectory;
        for (var depth = 0; depth < 4 && !string.IsNullOrEmpty(cursor); depth++)
        {
            yield return Path.Combine(cursor, ".data");
            yield return Path.Combine(cursor, "Output", ".data");
            cursor = Path.GetDirectoryName(cursor);
        }
    }

    /// <summary>Somewhere we can always write, for when the install folder is read-only.</summary>
    private static string WritableCacheDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "UEBulkExport", "native");

    // ------------------------------------------------------------------ zlib / oodle

    private static void SetUpZlib(string? explicitPath, List<string> searchDirs)
    {
        var path = Resolve(explicitPath, [ZlibHelper.DllName], searchDirs);

        if (path is null)
        {
            path = Path.Combine(DownloadTarget(), ZlibHelper.DllName);
            Log.Info($"downloading {ZlibHelper.DllName} ...");

            if (!ZlibHelper.DownloadDll(path, ZlibHelper.DOWNLOAD_URL))
            {
                Log.Warn("could not obtain zlib-ng; zlib-compressed chunks will fail to read");
                return;
            }
        }

        ZlibHelper.Initialize(path);
        Log.Info($"zlib-ng: {path}");
    }

    private static void SetUpOodle(string? explicitPath, List<string> searchDirs)
    {
        var path = Resolve(explicitPath, OodleNames, searchDirs);

        if (path is null)
        {
            Log.Info($"downloading {OodleHelper.OodleFileName} ...");
            path = DownloadOodle(Path.Combine(DownloadTarget(), OodleHelper.OodleFileName));

            if (path is null)
            {
                Log.Warn("could not obtain Oodle; Oodle-compressed chunks will fail to read.");
                Log.Warn($"Point --oodle at an existing {OodleHelper.OODLE_NAME_CURRENT} or {OodleHelper.OODLE_NAME_OLD}.");
                return;
            }
        }

        OodleHelper.Initialize(path);
        Log.Info($"oodle: {path}");
    }

    /// <summary>
    /// Returns where the library actually landed - the helper may redirect it - or null when
    /// neither source worked. The primary mirror goes down often enough to be worth a fallback.
    /// </summary>
    private static string? DownloadOodle(string destination)
    {
        var path = (string?) destination;
        if (OodleHelper.DownloadOodleDll(ref path) && path is not null) return path;

        try
        {
            using var client = new HttpClient();
            var downloaded = OodleHelper
                .DownloadOodleDllFromOodleUEAsync(client, destination, CancellationToken.None)
                .GetAwaiter().GetResult();

            return downloaded ? destination : null;
        }
        catch (Exception e)
        {
            Log.Warn($"Oodle fallback download failed: {e.Message}");
            return null;
        }
    }

    // ------------------------------------------------------------------ detex

    /// <summary>
    /// Detex decodes the BC/ETC/ASTC block formats CUE4Parse does not handle in managed code.
    /// CUE4Parse-Conversion carries a copy as an embedded resource, so unpack that when needed.
    /// </summary>
    private static void SetUpDetex(List<string> searchDirs)
    {
        var path = Resolve(null, [DetexHelper.DLL_NAME], searchDirs);

        if (path is not null)
        {
            DetexHelper.Initialize(path);
            Log.Info($"detex: {path}");
            return;
        }

        path = Path.Combine(DownloadTarget(), DetexHelper.DLL_NAME);

        if (DetexHelper.LoadDll(path))
        {
            DetexHelper.Initialize(path);
            Log.Info($"detex: {path} (unpacked)");
        }
        else
        {
            Log.Warn("could not obtain Detex; a few block-compressed textures will fail to decode");
        }
    }

    // ------------------------------------------------------------------ CUE4Parse-Natives

    /// <summary>
    /// ACL animation decompression is a P/Invoke into CUE4Parse-Natives. The build normally drops
    /// that library next to the executable; when it is missing - a bare clone, a copied exe -
    /// fetch it and tell the runtime where it went, instead of letting every animation fail.
    /// </summary>
    private static void SetUpCUE4ParseNatives(List<string> searchDirs, Options options)
    {
        _nativesPath = Resolve(null, [NativesLibrary + ".dll", "lib" + NativesLibrary + ".so"], searchDirs);

        if (_nativesPath is null && !options.DryRun && options.Mode is ExportMode.Full)
            _nativesPath = TryDownloadNatives();

        if (_nativesPath is null)
        {
            Log.Warn($"{NativesLibrary} not found; animations will fail to convert.");
            Log.Warn("Everything else still works. See the README for how to supply it.");
            return;
        }

        InstallResolver();
        Log.Info($"natives: {_nativesPath}");
    }

    private static string? TryDownloadNatives()
    {
        var destination = Path.Combine(WritableCacheDirectory, NativesLibrary + ".dll");

        try
        {
            Log.Info($"downloading {NativesLibrary} ...");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            using var package = new MemoryStream(client.GetByteArrayAsync(NativesPackageUrl).GetAwaiter().GetResult());
            using var archive = new ZipArchive(package, ZipArchiveMode.Read);

            var entry = archive.GetEntry(NativesEntryInPackage)
                        ?? throw new InvalidDataException($"{NativesEntryInPackage} is missing from the package");

            using (var source = entry.Open())
            using (var target = File.Create(destination))
                source.CopyTo(target);

            return destination;
        }
        catch (Exception e)
        {
            Log.Warn($"could not download {NativesLibrary}: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// The library may not sit next to the executable, so point the runtime at wherever it
    /// actually ended up. Both CUE4Parse assemblies declare imports against it.
    /// </summary>
    private static void InstallResolver()
    {
        if (_resolverInstalled) return;
        _resolverInstalled = true;

        foreach (var assembly in new[] { typeof(OodleHelper).Assembly, typeof(DetexHelper).Assembly })
        {
            try { NativeLibrary.SetDllImportResolver(assembly, ResolveNativeLibrary); }
            catch (InvalidOperationException) { /* a resolver is already installed for it */ }
        }
    }

    private static IntPtr ResolveNativeLibrary(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (_nativesPath is null || !name.Equals(NativesLibrary, StringComparison.OrdinalIgnoreCase))
            return IntPtr.Zero;

        return NativeLibrary.TryLoad(_nativesPath, out var handle) ? handle : IntPtr.Zero;
    }

    // ------------------------------------------------------------------ shared helpers

    /// <summary>Prefer the install folder, fall back to the user's cache when it is read-only.</summary>
    private static string DownloadTarget()
    {
        if (IsWritable(AppContext.BaseDirectory)) return AppContext.BaseDirectory;

        Directory.CreateDirectory(WritableCacheDirectory);
        return WritableCacheDirectory;
    }

    private static bool IsWritable(string directory)
    {
        try
        {
            var probe = Path.Combine(directory, $".write-probe-{Guid.NewGuid():N}");
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? Resolve(string? explicitPath, IEnumerable<string> names, List<string> searchDirs)
    {
        if (!string.IsNullOrEmpty(explicitPath))
        {
            if (File.Exists(explicitPath)) return explicitPath;
            throw new UserFacingException($"Native library not found: {explicitPath}");
        }

        return (from dir in searchDirs
                where !string.IsNullOrEmpty(dir) && Directory.Exists(dir)
                from name in names
                let candidate = Path.Combine(dir, name)
                where File.Exists(candidate)
                select candidate).FirstOrDefault();
    }
}
