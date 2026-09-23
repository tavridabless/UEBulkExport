namespace UEBulkExport;

/// <summary>A snapshot of a running export, raised every few seconds and once at the end.</summary>
public sealed record ExportProgress(
    int Processed,
    int Total,
    int Written,
    int Failed,
    TimeSpan Elapsed,
    TimeSpan? Eta,
    string Phase)
{
    public double Fraction => Total == 0 ? 0 : Math.Clamp((double) Processed / Total, 0, 1);
    public double RatePerSecond => Elapsed.TotalSeconds < 1 ? 0 : Processed / Elapsed.TotalSeconds;
}

/// <summary>What a run achieved. Exit code 0 means clean, 2 means some entries failed.</summary>
public sealed record ExportSummary(
    TimeSpan Elapsed,
    int Processed,
    int Exported,
    int Written,
    int IoStoreConverted,
    int NothingToDo,
    int NoConverter,
    int FailedEntries,
    int FailedObjects,
    string OutputDirectory,
    bool Cancelled)
{
    public int ExitCode => FailedEntries + FailedObjects == 0 && !Cancelled ? 0 : 2;
}

/// <summary>One mounted or locked container, as reported after <see cref="BulkExporter.Mount"/>.</summary>
public sealed record ContainerInfo(string Name, int FileCount, bool IsLocked, string? EncryptionKeyGuid);

/// <summary>What a container holds, grouped the way the <c>list</c> mode prints it.</summary>
public sealed record ContainerListing(
    IReadOnlyList<ContainerListing.ExtensionGroup> ByExtension,
    IReadOnlyList<ContainerListing.FolderGroup> TopFolders,
    int TotalCount,
    long TotalBytes)
{
    public sealed record ExtensionGroup(string Extension, int Count, long Bytes);
    public sealed record FolderGroup(string Folder, int Count);
}

/// <summary>What a run would do, as reported by <c>--dry-run</c>.</summary>
public sealed record ExportPlan(
    ExportMode Mode,
    string OutputDirectory,
    string? MappingsPath,
    int Selected,
    int AlreadyDone,
    int SkippedByMode,
    int Packages,
    int LooseFiles,
    int Payloads,
    int IoStorePackages);
