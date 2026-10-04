using CUE4Parse.Compression;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Readers;

namespace UEBulkExport.Tests;

/// <summary>Metadata-only fixture: these tests cannot read, mount or export real container content.</summary>
internal sealed class SyntheticGameFile(string path, long size = 1) : GameFile(path, size)
{
    public override bool IsEncrypted => false;
    public override CompressionMethod CompressionMethod => CompressionMethod.None;
    public override byte[] Read(FByteBulkDataHeader? header = null) => throw new NotSupportedException();
    public override FArchive CreateReader(FByteBulkDataHeader? header = null) => throw new NotSupportedException();
}
