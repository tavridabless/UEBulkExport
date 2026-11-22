using CUE4Parse.Compression;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Readers;

namespace UEBulkExport.Tests;

/// <summary>Synthetic entry that can optionally provide bytes without mounting a container.</summary>
internal sealed class SyntheticGameFile(string path, long size = 1, Func<byte[]>? read = null) : GameFile(path, size)
{
    public override bool IsEncrypted => false;
    public override CompressionMethod CompressionMethod => CompressionMethod.None;
    public override byte[] Read(FByteBulkDataHeader? header = null) => read?.Invoke() ?? throw new NotSupportedException();
    public override FArchive CreateReader(FByteBulkDataHeader? header = null) => throw new NotSupportedException();
}
