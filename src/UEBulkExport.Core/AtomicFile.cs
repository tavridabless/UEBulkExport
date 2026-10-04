namespace UEBulkExport;

/// <summary>Publishes files only after their complete contents have reached the disk.</summary>
internal static class AtomicFile
{
    internal const string TemporarySuffix = ".uebulk-part";

    public static void WriteAllBytes(string destination, byte[] data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = CreateTemporaryPath(destination);

        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, bufferSize: 1, FileOptions.None))
            {
                // The caller already owns the complete byte array: a per-file 1 MiB buffer
                // only copies small payloads and allocates memory for each output. One explicit
                // durable flush is sufficient before publication; WriteThrough would also
                // force each write fragment to disk before that final barrier.
                stream.Write(data);
                stream.Flush(flushToDisk: true);
            }

            // Unlike converter output, this temporary file has just been durably flushed.
            // Do not reopen and flush it a second time. Keep the rename after the closed handle.
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    public static string CreateTemporaryPath(string destination) =>
        destination + "." + Guid.NewGuid().ToString("N") + TemporarySuffix;

    public static void Publish(string stagedPath, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        // External converters have already closed the file. FlushFileBuffers through a new
        // handle before the rename so a journal record can never get ahead of the file data.
        // The handle needs write access: FileStream skips the flush on a read-only stream.
        using (var stream = new FileStream(stagedPath, FileMode.Open, FileAccess.ReadWrite,
                   FileShare.Read, bufferSize: 1, FileOptions.None))
            stream.Flush(flushToDisk: true);

        File.Move(stagedPath, destination, overwrite: true);
    }

    public static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
