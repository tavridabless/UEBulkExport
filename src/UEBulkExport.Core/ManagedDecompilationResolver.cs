using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using ICSharpCode.Decompiler.Metadata;

namespace UEBulkExport;

internal sealed record ManagedDecompilationReferences(IReadOnlyDictionary<string, string> Libraries,
    IReadOnlyList<string> Frameworks);

/// <summary>Finds installed framework metadata and matching local references without loading user assemblies.</summary>
internal sealed class ManagedDecompilationResolver(string framework, IReadOnlyList<string> folders, CancellationToken ct)
    : IAssemblyResolver, IDisposable
{
    private const int MaxReferences = 512;
    private const long MaxCopiedBytes = 512L * 1024 * 1024;
    private readonly UniversalAssemblyResolver _framework = new(null, false, framework, streamOptions: PEStreamOptions.PrefetchMetadata);
    private readonly Dictionary<string, Resolution> _resolved = new(StringComparer.OrdinalIgnoreCase);
    private readonly SortedSet<string> _unresolved = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    public IReadOnlyList<string> UnresolvedReferences => _unresolved.ToArray();

    public ManagedDecompilationReferences CollectReferences(PEFile module, List<string> diagnostics)
    {
        using var frameworkSnapshot = _framework.BeginSnapshot();
        var libraries = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var frameworks = new SortedSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { module.FullName };
        var pending = new Queue<PEFile>();
        pending.Enqueue(module);
        long copiedBytes = 0;
        while (pending.TryDequeue(out var current))
        {
            foreach (var reference in current.AssemblyReferences)
            {
                ct.ThrowIfCancellationRequested();
                if (!seen.Add(reference.FullName)) continue;
                var match = Find(reference);
                if (match.Module is not { } candidate)
                {
                    _unresolved.Add(reference.FullName);
                    continue;
                }
                if (match.RuntimePack is { } pack)
                {
                    if (pack != "Microsoft.NETCore.App") frameworks.Add(pack);
                    continue;
                }
                if (libraries.ContainsKey(reference.Name))
                {
                    _unresolved.Add(reference.FullName);
                    diagnostics.Add($"Several versions of {reference.Name} are referenced. The project includes the first local match.");
                    continue;
                }
                var bytes = new FileInfo(candidate.FileName).Length;
                if (bytes > MaxCopiedBytes - copiedBytes)
                {
                    _unresolved.Add(reference.FullName);
                    continue;
                }
                libraries.Add(reference.Name, candidate.FileName);
                copiedBytes += bytes;
                pending.Enqueue(candidate);
            }
        }
        return new ManagedDecompilationReferences(libraries, frameworks.ToArray());
    }

    public MetadataFile? Resolve(IAssemblyReference reference) => Find(reference).Module;
    public Task<MetadataFile?> ResolveAsync(IAssemblyReference reference) => Task.FromResult(Resolve(reference));
    public MetadataFile? ResolveModule(MetadataFile mainModule, string moduleName) => null;
    public Task<MetadataFile?> ResolveModuleAsync(MetadataFile mainModule, string moduleName) => Task.FromResult<MetadataFile?>(null);
    public IDisposable? BeginSnapshot() => _framework.BeginSnapshot();

    private Resolution Find(IAssemblyReference reference)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (reference.IsWindowsRuntime || !ManagedSourceDecompilation.IsSimpleName(reference.Name))
                return default;
            if (_resolved.TryGetValue(reference.FullName, out var cached)) return cached;
            if (_resolved.Count >= MaxReferences) return default;
            var match = FindFile(reference);
            _resolved.Add(reference.FullName, match);
            return match;
        }
    }

    private Resolution FindFile(IAssemblyReference reference)
    {
        // The framework resolver sees an identity without a requesting module or app .deps.json.
        // User-supplied libraries can only come from the original folder and explicit reference folders.
        var identity = AssemblyNameReference.Parse(reference.FullName);
        if (_framework.IsSharedAssembly(identity, out var pack) &&
            pack is "Microsoft.NETCore.App" or "Microsoft.AspNetCore.App" or "Microsoft.WindowsDesktop.App" &&
            _framework.FindAssemblyFile(identity) is { } installed && OpenMatching(installed, identity) is { } shared)
            return new Resolution(shared, pack);
        foreach (var folder in folders)
        {
            var dll = OpenMatching(Path.Combine(folder, identity.Name + ".dll"), identity);
            if (dll is not null) return new Resolution(dll, null);
            var exe = OpenMatching(Path.Combine(folder, identity.Name + ".exe"), identity);
            if (exe is not null) return new Resolution(exe, null);
        }
        return default;
    }

    private static PEFile? OpenMatching(string path, IAssemblyReference requested)
    {
        if (!File.Exists(path)) return null;
        PEFile? module = null;
        try
        {
            module = ManagedSourceDecompilation.OpenModule(path, PEStreamOptions.PrefetchMetadata);
            var reader = module.Metadata;
            if (reader.IsAssembly && reader.StringComparer.Equals(reader.GetAssemblyDefinition().Name, requested.Name, ignoreCase: true))
            {
                var actual = AssemblyNameReference.Parse(module.FullName);
                if (actual.Version == requested.Version &&
                    string.Equals(actual.Culture ?? "", requested.Culture ?? "", StringComparison.OrdinalIgnoreCase) &&
                    (actual.PublicKeyToken ?? []).AsSpan().SequenceEqual(requested.PublicKeyToken ?? []))
                    return module;
            }
        }
        catch (Exception error) when (error is BadImageFormatException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            // A same-name file may be native, malformed or a different assembly. Continue looking.
        }
        module?.Dispose();
        return null;
    }

    public void Dispose()
    {
        foreach (var resolution in _resolved.Values) resolution.Module?.Dispose();
    }

    private readonly record struct Resolution(PEFile? Module, string? RuntimePack);
}
