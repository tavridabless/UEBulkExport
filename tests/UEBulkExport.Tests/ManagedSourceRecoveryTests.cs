using System.IO.Compression;
using System.Text;

namespace UEBulkExport.Tests;

public sealed class ManagedSourceRecoveryTests
{
    [Fact]
    public void Embedded_formats_preserve_bytes_and_validate_the_declared_size()
    {
        var source = Encoding.UTF8.GetBytes("// Fixture Ω\r\npublic class Signal {}\r\n");
        using var packed = new MemoryStream();
        using (var deflate = new DeflateStream(packed, CompressionMode.Compress, leaveOpen: true))
            deflate.Write(source);
        Assert.Equal(source, ManagedSourceRecovery.Decode(0, source));
        Assert.Equal(source, ManagedSourceRecovery.Decode(source.Length, packed.ToArray()));
        Assert.Null(ManagedSourceRecovery.Decode(source.Length + 1, packed.ToArray()));
        Assert.Null(ManagedSourceRecovery.Decode(source.Length - 1, packed.ToArray()));
        Assert.Null(ManagedSourceRecovery.Decode(-1, packed.ToArray()));
    }

    [Theory]
    [InlineData("D:\\private\\workspace\\Code.cs", "Code.cs")]
    [InlineData("../../Source/Thing.cs", "Thing.cs")]
    [InlineData("some/..", "source.cs")]
    [InlineData(@"//source/a:b.cs", "a_b.cs")]
    public void Documents_get_a_portable_output_basename(string input, string expected) =>
        Assert.Equal(expected, ManagedSourceRecovery.FileName(input));
}
