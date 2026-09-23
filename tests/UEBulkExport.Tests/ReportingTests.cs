using CUE4Parse_Conversion.Options;

namespace UEBulkExport.Tests;

public sealed class ExportProgressTests
{
    private static ExportProgress Progress(int processed, int total, TimeSpan elapsed) =>
        new(processed, total, Written: 0, Failed: 0, elapsed, Eta: null, Phase: "test");

    [Fact]
    public void Fraction_is_zero_when_total_is_zero()
    {
        Assert.Equal(0, Progress(5, 0, TimeSpan.Zero).Fraction);
    }

    [Fact]
    public void Fraction_is_processed_over_total()
    {
        Assert.Equal(0.25, Progress(25, 100, TimeSpan.Zero).Fraction);
    }

    [Fact]
    public void Fraction_is_clamped_to_one()
    {
        Assert.Equal(1, Progress(150, 100, TimeSpan.Zero).Fraction);
    }

    [Fact]
    public void Rate_is_zero_before_the_first_second()
    {
        Assert.Equal(0, Progress(50, 100, TimeSpan.FromMilliseconds(999)).RatePerSecond);
    }

    [Fact]
    public void Rate_is_processed_per_elapsed_second()
    {
        Assert.Equal(10, Progress(100, 100, TimeSpan.FromSeconds(10)).RatePerSecond);
    }
}

public sealed class ExportSummaryTests
{
    private static ExportSummary Summary(
        int failedEntries = 0, int failedObjects = 0, bool cancelled = false) =>
        new(TimeSpan.FromSeconds(1), Processed: 10, Exported: 10, Written: 10, IoStoreConverted: 0,
            NothingToDo: 0, NoConverter: 0, failedEntries, failedObjects, OutputDirectory: "out", cancelled);

    [Fact]
    public void Clean_run_exits_zero()
    {
        Assert.Equal(0, Summary().ExitCode);
    }

    [Fact]
    public void Failed_entries_exit_two()
    {
        Assert.Equal(2, Summary(failedEntries: 1).ExitCode);
    }

    [Fact]
    public void Failed_objects_exit_two()
    {
        Assert.Equal(2, Summary(failedObjects: 1).ExitCode);
    }

    [Fact]
    public void Cancelled_run_exits_two_even_without_failures()
    {
        Assert.Equal(2, Summary(cancelled: true).ExitCode);
    }
}

public sealed class OptionsAnimFormatTests
{
    [Fact]
    public void Gltf_mesh_falls_back_to_actorx_for_animations()
    {
        var o = new Options { MeshFormat = EMeshFormat.Gltf2 };
        Assert.Equal(EMeshFormat.ActorX, o.AnimFormat);
    }

    [Theory]
    [InlineData(EMeshFormat.USD)]
    [InlineData(EMeshFormat.ActorX)]
    [InlineData(EMeshFormat.UEFormat)]
    public void Non_gltf_mesh_format_is_used_for_animations_too(EMeshFormat format)
    {
        var o = new Options { MeshFormat = format };
        Assert.Equal(format, o.AnimFormat);
    }

    [Fact]
    public void Explicit_override_wins()
    {
        var o = new Options { MeshFormat = EMeshFormat.Gltf2, AnimFormatOverride = EMeshFormat.UEFormat };
        Assert.Equal(EMeshFormat.UEFormat, o.AnimFormat);
    }

    [Theory]
    [InlineData(ExportMode.Full, true)]
    [InlineData(ExportMode.Json, true)]
    [InlineData(ExportMode.Legacy, false)]
    [InlineData(ExportMode.Raw, false)]
    [InlineData(ExportMode.List, false)]
    public void RequiresMappings_only_for_parsing_modes(ExportMode mode, bool expected)
    {
        Assert.Equal(expected, new Options { Mode = mode }.RequiresMappings);
    }
}

public sealed class BulkExporterSafeNameTests
{
    [Fact]
    public void Plain_names_are_unchanged()
    {
        Assert.Equal("SK_Mannequin.001", BulkExporter.SafeName("SK_Mannequin.001"));
    }

    [Fact]
    public void Slash_and_nul_are_replaced_with_underscores()
    {
        Assert.Equal("a_b_c", BulkExporter.SafeName("a/b\0c"));
    }

    [Fact]
    public void Result_contains_no_invalid_filename_characters()
    {
        var dirty = "we:ird*na?me<with>|bad\"chars\\and/slashes";

        var safe = BulkExporter.SafeName(dirty);

        Assert.Equal(dirty.Length, safe.Length);
        Assert.DoesNotContain(safe, c => Path.GetInvalidFileNameChars().Contains(c));
    }
}
