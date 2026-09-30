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
    public void Enabled_valid_plugin_loads_its_page()
    {
        var folder = _tmp.Dir("loaded");
        File.Copy(typeof(LoadedTestPlugin).Assembly.Location, Path.Combine(folder, "Plugin.dll"));
        WriteManifest(folder, Manifest("loaded.plugin") with
        {
            EntryType = typeof(LoadedTestPlugin).FullName!
        });
        var settings = new AppSettings();
        settings.EnabledPlugins.Add("loaded.plugin");

        var catalog = new PluginCatalog(settings, [_tmp.Path]);
        var plugin = Assert.Single(catalog.Plugins);

        Assert.True(plugin.IsLoaded, plugin.LoadError);
    }

    private void CreatePlugin(string id, string folderName)
    {
        var folder = _tmp.Dir(folderName);
        File.WriteAllBytes(Path.Combine(folder, "Plugin.dll"), [0x4d, 0x5a]);
        WriteManifest(folder, Manifest(id));
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
        string EntryType);
}

public sealed class LoadedTestPlugin : IUEBulkExportPlugin
{
    public Control CreatePage(IUEBulkExportPluginContext context) => new TextBlock { Text = context.HostVersion };
}
