namespace UEBulkExport.Tests;

public sealed class AtomicFileTests
{
    [Fact]
    public void WriteAllBytes_replaces_the_destination_without_leaving_a_partial_file()
    {
        using var temp = new TempDir();
        var path = temp.File("Output", "asset.uasset");
        File.WriteAllText(path, "old");

        AtomicFile.WriteAllBytes(path, [1, 2, 3, 4]);

        Assert.Equal([1, 2, 3, 4], File.ReadAllBytes(path));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(path)!,
            "*" + AtomicFile.TemporarySuffix));
    }

    [Fact]
    public void Publish_atomically_replaces_an_existing_file()
    {
        using var temp = new TempDir();
        var destination = temp.File("Output", "asset.uexp");
        File.WriteAllText(destination, "old");
        var staged = temp.File("Stage", "asset.uexp");
        File.WriteAllText(staged, "complete");

        AtomicFile.Publish(staged, destination);

        Assert.Equal("complete", File.ReadAllText(destination));
        Assert.False(File.Exists(staged));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4095)]
    [InlineData(4096)]
    [InlineData(1048575)]
    [InlineData(1048576)]
    [InlineData(1048587)]
    public void Unbuffered_output_preserves_all_bytes_across_former_buffer_boundaries(int length)
    {
        using var temp = new TempDir();
        var path = System.IO.Path.Combine(temp.Path, "new", "nested", "asset.bin");
        var bytes = new byte[length];
        new Random(523).NextBytes(bytes);

        AtomicFile.WriteAllBytes(path, bytes);

        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Empty(Directory.EnumerateFiles(temp.Path, "*" + AtomicFile.TemporarySuffix,
            SearchOption.AllDirectories));
    }

    [Fact]
    public void Failed_rename_preserves_the_old_file_and_cleans_its_owned_temporary()
    {
        if (!OperatingSystem.IsWindows()) return; // Windows sharing prevents replacement.
        using var temp = new TempDir();
        var path = temp.File("Output", "asset.bin");
        File.WriteAllBytes(path, [9, 8]);
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var error = Record.Exception(() => AtomicFile.WriteAllBytes(path, [1, 2, 3]));
            Assert.True(error is IOException or UnauthorizedAccessException,
                $"Expected a denied Windows rename, got {error?.GetType().Name ?? "success"}.");
        }

        Assert.Equal([9, 8], File.ReadAllBytes(path));
        Assert.Empty(Directory.EnumerateFiles(temp.Path, "*" + AtomicFile.TemporarySuffix,
            SearchOption.AllDirectories));
    }

    [Fact]
    public void Converter_output_is_not_published_when_its_durable_flush_handle_cannot_open()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = new TempDir();
        var destination = temp.File("Output", "asset.bin");
        File.WriteAllBytes(destination, [7]);
        var staged = temp.File("Stage", "asset.bin");
        File.WriteAllBytes(staged, [1, 2, 3]);

        using (var locked = new FileStream(staged, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.ThrowsAny<IOException>(() => AtomicFile.Publish(staged, destination));

        Assert.Equal([7], File.ReadAllBytes(destination));
        Assert.Equal([1, 2, 3], File.ReadAllBytes(staged));
    }

    [Fact]
    public async Task Concurrent_outputs_in_the_same_folder_have_independent_temporaries()
    {
        using var temp = new TempDir();
        var directory = System.IO.Path.Combine(temp.Path, "shared");
        await Parallel.ForEachAsync(Enumerable.Range(0, 32), new ParallelOptions { MaxDegreeOfParallelism = 4 },
            (index, _) =>
            {
                AtomicFile.WriteAllBytes(System.IO.Path.Combine(directory, index + ".bin"), [(byte)index]);
                return ValueTask.CompletedTask;
            });

        for (var index = 0; index < 32; index++)
            Assert.Equal([(byte)index], File.ReadAllBytes(System.IO.Path.Combine(directory, index + ".bin")));
        Assert.Equal(32, Directory.GetFiles(directory).Length);
    }
}
