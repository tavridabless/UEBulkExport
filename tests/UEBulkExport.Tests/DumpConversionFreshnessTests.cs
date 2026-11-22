namespace UEBulkExport.Tests;

public sealed class DumpConversionFreshnessTests
{
    [Fact]
    public async Task A_same_size_cooked_payload_change_selects_a_new_stage_even_with_the_same_timestamp()
    {
        using var temp = new TempDir();
        var source = temp.Dir("Source");
        var export = temp.Dir("Work", "Exported");
        var main = Path.Combine(source, "Texture.uasset");
        var payload = Path.Combine(source, "Texture.ubulk");
        var legacy = Path.Combine(export, "Legacy.png");
        File.WriteAllBytes(main, [1, 2]);
        File.WriteAllBytes(payload, [3, 4]);
        File.WriteAllBytes(legacy, [5]);
        var modified = File.GetLastWriteTimeUtc(payload);
        var options = new DumpConversionOptions(source, "4.27", "", "", "", "/Game/Test");

        var first = await DumpConversionFreshness.OpenCookedStageAsync(export, source, [main, payload], options, default);
        var unchanged = await DumpConversionFreshness.OpenCookedStageAsync(export, source, [payload, main], options, default);
        File.WriteAllBytes(payload, [3, 9]);
        File.SetLastWriteTimeUtc(payload, modified);
        var updated = await DumpConversionFreshness.OpenCookedStageAsync(export, source, [main, payload], options, default);

        Assert.Equal(first, unchanged);
        Assert.NotEqual(first, updated);
        Assert.True(Directory.Exists(first));
        DumpConversionFreshness.PruneCookedStages(updated);
        Assert.False(Directory.Exists(first));
        Assert.True(Directory.Exists(updated));
        Assert.True(File.Exists(legacy));
    }

    [Fact]
    public async Task A_gltf_fingerprint_covers_local_buffers_and_is_independent_of_the_stage_directory()
    {
        using var temp = new TempDir();
        var first = temp.Dir("Revision1");
        var next = temp.Dir("Revision2");
        var firstFile = Path.Combine(first, "Mesh.gltf");
        var nextFile = Path.Combine(next, "Mesh.gltf");
        const string gltf = """{"buffers":[{"uri":"mesh%20data.bin"}]}""";
        File.WriteAllText(firstFile, gltf);
        File.WriteAllText(nextFile, gltf);
        File.WriteAllBytes(Path.Combine(first, "mesh data.bin"), [1, 2]);
        var nextBuffer = Path.Combine(next, "mesh data.bin");
        File.WriteAllBytes(nextBuffer, [1, 2]);

        var earlier = await DumpConversionFreshness.FingerprintImportAsync(firstFile, first, default);
        var unchanged = await DumpConversionFreshness.FingerprintImportAsync(nextFile, next, default);
        File.WriteAllBytes(nextBuffer, [1, 3]);
        var changed = await DumpConversionFreshness.FingerprintImportAsync(nextFile, next, default);

        Assert.Equal(earlier, unchanged);
        Assert.NotEqual(earlier, changed);
    }
}
