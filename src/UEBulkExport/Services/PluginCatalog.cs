using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using UEBulkExport.Plugin.Abstractions;

namespace UEBulkExport.Gui.Services;

public sealed class PluginManifest
{
    public int SchemaVersion { get; set; }
    public int ApiVersion { get; set; }
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Version { get; set; } = "";
    public string Author { get; set; } = "";
    public string Homepage { get; set; } = "";
    public string EntryAssembly { get; set; } = "";
    public string EntryType { get; set; } = "";
    public string Icon { get; set; } = "";
    public string NavigationLabel { get; set; } = "";
    public string NavigationIcon { get; set; } = "";
    public string MinimumHostVersion { get; set; } = "";
    public string MaximumHostVersion { get; set; } = "";
}

public sealed class PluginDescriptor
{
    internal PluginDescriptor(string directory, PluginManifest manifest)
    {
        Directory = directory;
        Manifest = manifest;
    }

    public string Directory { get; }
    public PluginManifest Manifest { get; }
    public string? ValidationError { get; internal set; }
    public string? LoadError { get; internal set; }
    public bool IsEnabled { get; internal set; }
    public bool WasEnabledAtStartup { get; internal set; }
    public Control? Page { get; internal set; }
    public bool IsLoaded => Page is not null;
    public bool IsValid => ValidationError is null;
    public string PageKey => $"plugin:{Manifest.Id}";
    public string NavigationLabel => string.IsNullOrWhiteSpace(Manifest.NavigationLabel)
        ? Manifest.Name
        : Manifest.NavigationLabel;
    public string NavigationIcon => string.IsNullOrWhiteSpace(Manifest.NavigationIcon)
        ? PluginCatalog.DefaultNavigationIcon
        : Manifest.NavigationIcon;
}

/// <summary>
/// Discovers metadata without loading code. Only plugins explicitly enabled in per-user settings
/// are loaded, once, during startup. Desktop plugins are full-trust code and are never sandboxed.
/// </summary>
public sealed class PluginCatalog
{
    public const string ManifestFileName = "plugin.json";
    public const string DefaultNavigationIcon =
        "M8 3 L16 3 L16 7 L20 7 L20 15 L16 15 L16 21 L8 21 L8 17 L4 17 L4 9 L8 9 Z M8 9 L12 9 L12 5 M16 15 L12 15 L12 19";

    private const long MaximumManifestBytes = 64 * 1024;
    private const long MaximumEntryAssemblyBytes = 128L * 1024 * 1024;
    private static readonly Regex IdPattern = new("^[a-z0-9][a-z0-9._-]{2,63}$", RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly AppSettings _settings;
    private readonly List<PluginLoadContext> _loadContexts = [];

    public static string ApplicationPluginsDirectory => Path.Combine(AppContext.BaseDirectory, "plugins");
    public static string UserPluginsDirectory => Path.Combine(AppSettings.DataDirectory, "plugins");
    public IReadOnlyList<PluginDescriptor> Plugins { get; }

    public PluginCatalog(AppSettings settings, IEnumerable<string>? roots = null, bool loadEnabled = true)
    {
        _settings = settings;
        Plugins = Discover(roots ?? [ApplicationPluginsDirectory, UserPluginsDirectory]);

        var enabled = settings.EnabledPlugins.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var plugin in Plugins)
        {
            plugin.IsEnabled = enabled.Contains(plugin.Manifest.Id);
            plugin.WasEnabledAtStartup = plugin.IsEnabled;
            if (loadEnabled && plugin.IsEnabled && plugin.IsValid) Load(plugin);
        }
    }

    public void SetEnabled(PluginDescriptor plugin, bool enabled)
    {
        plugin.IsEnabled = enabled;
        _settings.EnabledPlugins.RemoveAll(id => id.Equals(plugin.Manifest.Id, StringComparison.OrdinalIgnoreCase));
        if (enabled) _settings.EnabledPlugins.Add(plugin.Manifest.Id);
        _settings.Save();
    }

    public static IReadOnlyList<PluginDescriptor> Discover(IEnumerable<string> roots)
    {
        var found = new List<PluginDescriptor>();
        foreach (var rootCandidate in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string root;
            try { root = Path.GetFullPath(rootCandidate); }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
            {
                Log.Warn($"invalid plugin root '{rootCandidate}': {e.Message}");
                continue;
            }

            if (!System.IO.Directory.Exists(root)) continue;

            IEnumerable<string> directories;
            try { directories = System.IO.Directory.EnumerateDirectories(root).Order(StringComparer.OrdinalIgnoreCase).ToArray(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"could not enumerate plugin root '{root}': {e.Message}");
                continue;
            }

            foreach (var directory in directories)
            {
                var manifestPath = Path.Combine(directory, ManifestFileName);
                if (!File.Exists(manifestPath)) continue;
                found.Add(ReadManifest(directory, manifestPath));
            }
        }

        foreach (var duplicate in found.Where(p => !string.IsNullOrWhiteSpace(p.Manifest.Id))
                     .GroupBy(p => p.Manifest.Id, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            foreach (var plugin in duplicate)
                plugin.ValidationError = $"Duplicate plugin id '{duplicate.Key}'. Remove or rename one copy.";
        }

        return found;
    }

    private static PluginDescriptor ReadManifest(string directory, string path)
    {
        var fallback = new PluginManifest { Name = Path.GetFileName(directory), Id = Path.GetFileName(directory) };
        var descriptor = new PluginDescriptor(directory, fallback);

        try
        {
            var info = new FileInfo(path);
            if (info.Length is <= 0 or > MaximumManifestBytes)
                throw new InvalidDataException("plugin.json must be between 1 byte and 64 KiB.");

            var manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(path), JsonOptions)
                           ?? throw new InvalidDataException("plugin.json is empty.");
            Normalize(manifest);
            descriptor = new PluginDescriptor(directory, manifest);
            descriptor.ValidationError = Validate(manifest, directory);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or
                                      InvalidDataException or ArgumentException or NotSupportedException)
        {
            descriptor.ValidationError = e.Message;
        }

        return descriptor;
    }

    private static void Normalize(PluginManifest manifest)
    {
        manifest.Id ??= "";
        manifest.Name ??= "";
        manifest.Description ??= "";
        manifest.Version ??= "";
        manifest.Author ??= "";
        manifest.Homepage ??= "";
        manifest.EntryAssembly ??= "";
        manifest.EntryType ??= "";
        manifest.Icon ??= "";
        manifest.NavigationLabel ??= "";
        manifest.NavigationIcon ??= "";
        manifest.MinimumHostVersion ??= "";
        manifest.MaximumHostVersion ??= "";
    }

    private static string? Validate(PluginManifest manifest, string directory)
    {
        if (manifest.SchemaVersion != 1) return $"Unsupported manifest schema {manifest.SchemaVersion}.";
        if (manifest.ApiVersion != PluginApi.Version) return $"Unsupported plugin API {manifest.ApiVersion}.";
        if (!IdPattern.IsMatch(manifest.Id)) return "Plugin id must contain 3-64 lowercase letters, digits, '.', '_' or '-'.";
        if (string.IsNullOrWhiteSpace(manifest.Name) || manifest.Name.Length > 80) return "Plugin name must contain 1-80 characters.";
        if (manifest.Description.Length > 600) return "Plugin description is longer than 600 characters.";
        if (manifest.Author.Length > 120) return "Plugin author is longer than 120 characters.";
        if (!System.Version.TryParse(manifest.Version, out _)) return "Plugin version is not valid.";
        if (string.IsNullOrWhiteSpace(manifest.EntryType) || manifest.EntryType.Length > 300) return "Plugin entryType is invalid.";

        if (!TryDirectChild(directory, manifest.EntryAssembly, ".dll", out var assemblyPath))
            return "entryAssembly must name a DLL directly inside the plugin folder.";
        if (!File.Exists(assemblyPath)) return $"Entry assembly '{manifest.EntryAssembly}' is missing.";
        if (new FileInfo(assemblyPath).Length > MaximumEntryAssemblyBytes) return "Entry assembly is larger than 128 MiB.";

        if (!string.IsNullOrWhiteSpace(manifest.Icon))
        {
            var extension = Path.GetExtension(manifest.Icon).ToLowerInvariant();
            if (extension is not (".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp") ||
                !TryDirectChild(directory, manifest.Icon, extension, out var iconPath))
                return "Icon must be a PNG, JPEG, WEBP or BMP file directly inside the plugin folder.";
            if (!File.Exists(iconPath)) return $"Plugin icon '{manifest.Icon}' is missing.";
            if (new FileInfo(iconPath).Length > 2 * 1024 * 1024) return "Plugin icon is larger than 2 MiB.";
        }

        if (!string.IsNullOrWhiteSpace(manifest.Homepage) &&
            (!Uri.TryCreate(manifest.Homepage, UriKind.Absolute, out var homepage) || homepage.Scheme is not ("http" or "https")))
            return "Plugin homepage must be an absolute HTTP or HTTPS URL.";

        if (!TryHostVersion(manifest.MinimumHostVersion, out var minimum)) return "minimumHostVersion is invalid.";
        if (!TryHostVersion(manifest.MaximumHostVersion, out var maximum)) return "maximumHostVersion is invalid.";
        var host = Assembly.GetEntryAssembly()?.GetName().Version ?? new System.Version(0, 0);
        if (minimum is not null && host < minimum) return $"Requires UEBulkExport {minimum} or newer.";
        if (maximum is not null && host > maximum) return $"Supports UEBulkExport only through {maximum}.";

        if (manifest.NavigationIcon.Length > 4096 || manifest.NavigationIcon.Any(char.IsControl))
            return "navigationIcon is too long or contains control characters.";

        return null;
    }

    private static bool TryDirectChild(string directory, string fileName, string extension, out string fullPath)
    {
        fullPath = "";
        if (string.IsNullOrWhiteSpace(fileName) || Path.IsPathRooted(fileName) ||
            !fileName.Equals(Path.GetFileName(fileName), StringComparison.Ordinal) ||
            !Path.GetExtension(fileName).Equals(extension, StringComparison.OrdinalIgnoreCase)) return false;
        fullPath = Path.GetFullPath(Path.Combine(directory, fileName));
        return true;
    }

    private static bool TryHostVersion(string value, out System.Version? version)
    {
        version = null;
        return string.IsNullOrWhiteSpace(value) || System.Version.TryParse(value, out version);
    }

    private void Load(PluginDescriptor plugin)
    {
        try
        {
            var assemblyPath = Path.GetFullPath(Path.Combine(plugin.Directory, plugin.Manifest.EntryAssembly));
            var context = new PluginLoadContext(plugin.Manifest.Id, assemblyPath);
            var assembly = context.LoadFromAssemblyPath(assemblyPath);
            var type = assembly.GetType(plugin.Manifest.EntryType, throwOnError: true, ignoreCase: false)!;
            if (!typeof(IUEBulkExportPlugin).IsAssignableFrom(type) || type.IsAbstract)
                throw new InvalidDataException($"{plugin.Manifest.EntryType} does not implement IUEBulkExportPlugin.");

            var instance = Activator.CreateInstance(type) as IUEBulkExportPlugin
                           ?? throw new InvalidDataException("Plugin entry point could not be constructed.");
            plugin.Page = instance.CreatePage(new PluginHostContext(plugin));
            if (plugin.Page is null) throw new InvalidDataException("Plugin returned no page.");
            _loadContexts.Add(context);
            Log.Info($"plugin loaded: {plugin.Manifest.Name} {plugin.Manifest.Version}");
        }
        catch (Exception e)
        {
            plugin.LoadError = e.GetBaseException().Message;
            Log.Warn($"plugin '{plugin.Manifest.Id}' was not loaded: {plugin.LoadError}");
        }
    }

    private sealed class PluginHostContext(PluginDescriptor plugin) : IUEBulkExportPluginContext
    {
        public string HostVersion => Cli.Version;
        public string PluginDirectory => plugin.Directory;

        public void Log(PluginLogLevel level, string message)
        {
            var prefixed = $"plugin {plugin.Manifest.Id}: {message}";
            switch (level)
            {
                case PluginLogLevel.Warning: UEBulkExport.Log.Warn(prefixed); break;
                case PluginLogLevel.Error: UEBulkExport.Log.Error(prefixed); break;
                default: UEBulkExport.Log.Info(prefixed); break;
            }
        }

        public void OpenFolder(string path)
        {
            var fullPath = Path.GetFullPath(path);
            var root = plugin.Directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
                !fullPath.Equals(plugin.Directory, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("A plugin may only open a folder inside its own directory through the host API.");
            ShellHelper.OpenFolder(fullPath);
        }
    }
}

internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string id, string mainAssemblyPath) : base($"UEBulkExport.Plugin.{id}", isCollectible: false)
    {
        _resolver = new AssemblyDependencyResolver(mainAssemblyPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var shared = Default.Assemblies.FirstOrDefault(assembly =>
            AssemblyName.ReferenceMatchesDefinition(assembly.GetName(), assemblyName));
        if (shared is not null) return shared;

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? 0 : LoadUnmanagedDllFromPath(path);
    }
}
