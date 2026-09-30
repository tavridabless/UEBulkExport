using System.Text.Json;
using Avalonia.Controls;
using UEBulkExport.Gui.Services;
using UEBulkExport.Plugin.Abstractions;

namespace UEBulkExport.Tests;

public sealed class PluginCatalogTests : IDisposable
{
    private readonly TempDir _tmp = new();
    public void Dispose() => _tmp.Dispose();

    [Fact]
    public void Valid_disabled_plugin_is_discovered_without_loading_its_dll()
    {
        CreatePlugin("sample.plugin", "sample");
        var settings = new AppSettings();

        var catalog = new PluginCatalog(settings, [_tmp.Path]);

        var plugin = Assert.Single(catalog.Plugins);
        Assert.True(plugin.IsValid, plugin.ValidationError);
        Assert.False(plugin.IsEnabled);
        Assert.False(plugin.IsLoaded);
        Assert.Null(plugin.LoadError);
    }

    [Fact]
    public void Entry_assembly_cannot_escape_plugin_folder()
    {
        var folder = _tmp.Dir("escape");
        WriteManifest(folder, Manifest("escape.plugin") with { EntryAssembly = "..\\outside.dll" });

        var plugin = Assert.Single(PluginCatalog.Discover([_tmp.Path]));

        Assert.False(plugin.IsValid);
        Assert.Contains("directly inside", plugin.ValidationError);
    }

    [Fact]
    public void Duplicate_ids_are_all_rejected()
    {
        CreatePlugin("same.plugin", "first");
        CreatePlugin("same.plugin", "second");

        var plugins = PluginCatalog.Discover([_tmp.Path]);

        Assert.Equal(2, plugins.Count);
        Assert.All(plugins, plugin => Assert.Contains("Duplicate plugin id", plugin.ValidationError));
    }

    [Fact]
    public void Invalid_manifest_does_not_prevent_other_plugins_from_being_discovered()
    {
        CreatePlugin("good.plugin", "good");
        var broken = _tmp.Dir("broken");
        File.WriteAllText(Path.Combine(broken, PluginCatalog.ManifestFileName), "{not-json");

        var plugins = PluginCatalog.Discover([_tmp.Path]);

        Assert.Equal(2, plugins.Count);
        Assert.Single(plugins, plugin => plugin.IsValid);
        Assert.Single(plugins, plugin => !plugin.IsValid);
    }

    [Fact]
    public void Null_manifest_strings_are_rejected_without_crashing_discovery()
    {
        var folder = _tmp.Dir("null-fields");
        File.WriteAllText(Path.Combine(folder, PluginCatalog.ManifestFileName),
            """{"schemaVersion":1,"apiVersion":1,"id":null,"name":null,"version":null}""");

        var plugin = Assert.Single(PluginCatalog.Discover([_tmp.Path]));

        Assert.False(plugin.IsValid);
        Assert.False(string.IsNullOrWhiteSpace(plugin.ValidationError));
    }

    [Fact]
    public void Disabled_plugin_does_not_grant_declared_capability()
    {
        CreatePlugin("sample.plugin", "sample", [PluginCatalog.AesKeysCapability]);

        var catalog = new PluginCatalog(new AppSettings(), [_tmp.Path]);

        Assert.False(catalog.HasCapability(PluginCatalog.AesKeysCapability));
    }

    [Fact]
    public void Enabled_plugin_grants_capability_only_after_successful_load_and_can_hide_its_page()
    {
        var folder = _tmp.Dir("loaded");
        File.Copy(typeof(LoadedTestPlugin).Assembly.Location, Path.Combine(folder, "Plugin.dll"));
        WriteManifest(folder, Manifest("loaded.plugin") with
        {
            EntryType = typeof(LoadedTestPlugin).FullName!,
            Capabilities = [PluginCatalog.AesKeysCapability],
            ShowPage = false
        });
        var settings = new AppSettings();
        settings.EnabledPlugins.Add("loaded.plugin");

        var catalog = new PluginCatalog(settings, [_tmp.Path]);
        var plugin = Assert.Single(catalog.Plugins);

        Assert.True(plugin.IsLoaded, plugin.LoadError);
        Assert.True(catalog.HasCapability(PluginCatalog.AesKeysCapability));
        Assert.False(plugin.ShowsPage);
    }

    [Fact]
    public void Invalid_or_duplicate_capabilities_are_rejected()
    {
        var invalid = _tmp.Dir("invalid-capability");
        File.WriteAllBytes(Path.Combine(invalid, "Plugin.dll"), [0x4d, 0x5a]);
        WriteManifest(invalid, Manifest("invalid.plugin") with { Capabilities = ["AES Keys"] });
        var duplicate = _tmp.Dir("duplicate-capability");
        File.WriteAllBytes(Path.Combine(duplicate, "Plugin.dll"), [0x4d, 0x5a]);
        WriteManifest(duplicate, Manifest("duplicate.plugin") with { Capabilities = ["aes-keys", "aes-keys"] });

        var plugins = PluginCatalog.Discover([_tmp.Path]);

        Assert.All(plugins, plugin => Assert.False(plugin.IsValid));
        Assert.Contains(plugins, plugin => plugin.ValidationError!.Contains("valid lowercase", StringComparison.Ordinal));
        Assert.Contains(plugins, plugin => plugin.ValidationError!.Contains("duplicates", StringComparison.Ordinal));
    }

    private void CreatePlugin(string id, string folderName, string[]? capabilities = null)
    {
        var folder = _tmp.Dir(folderName);
        File.WriteAllBytes(Path.Combine(folder, "Plugin.dll"), [0x4d, 0x5a]);
        WriteManifest(folder, Manifest(id) with { Capabilities = capabilities });
    }

    private static TestManifest Manifest(string id) => new(
        1,
        1,
        id,
        "Test plugin",
        "Metadata-only test plugin.",
        "1.0.0",
        "Tests",
        "Plugin.dll",
        "Tests.Plugin");

    private static void WriteManifest(string folder, TestManifest manifest) =>
        File.WriteAllText(Path.Combine(folder, PluginCatalog.ManifestFileName),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

    private sealed record TestManifest(
        int SchemaVersion,
        int ApiVersion,
        string Id,
        string Name,
        string Description,
        string Version,
        string Author,
        string EntryAssembly,
        string EntryType,
        string[]? Capabilities = null,
        bool? ShowPage = null);
}

public sealed class LoadedTestPlugin : IUEBulkExportPlugin
{
    public Control CreatePage(IUEBulkExportPluginContext context) => new TextBlock { Text = context.HostVersion };
}
