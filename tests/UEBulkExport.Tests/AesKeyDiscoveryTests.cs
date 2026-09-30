using System.Buffers.Binary;
using System.Text;

namespace UEBulkExport.Tests;

public sealed class AesKeyScannerTests : IDisposable
{
    private const string Hex = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";
    private readonly TempDir _tmp = new();

    public void Dispose() => _tmp.Dispose();

    [Fact]
    public async Task Finds_ascii_hex_and_deduplicates_repeated_values()
    {
        var path = Write("ascii.bin", Encoding.ASCII.GetBytes($"first=0x{Hex}\0second={Hex}\0"));

        var result = await AesKeyScanner.ScanAsync(path);

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("0x" + Hex, candidate.Key);
        Assert.Equal(AesKeyOrigin.AsciiHex, candidate.Origin);
        Assert.Equal(8, candidate.Offset);
    }

    [Fact]
    public async Task Finds_utf16_hex()
    {
        var path = Write("utf16.bin", Encoding.Unicode.GetBytes($"EncryptionKey=0x{Hex}\0"));

        var candidate = Assert.Single((await AesKeyScanner.ScanAsync(path)).Candidates);

        Assert.Equal("0x" + Hex, candidate.Key);
        Assert.Equal(AesKeyOrigin.Utf16Hex, candidate.Origin);
    }

    [Fact]
    public async Task Finds_labelled_base64_but_ignores_unlabelled_base64()
    {
        var key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        var encoded = Convert.ToBase64String(key);
        var path = Write("base64.bin", Encoding.ASCII.GetBytes($"{encoded}\0EncryptionKey={encoded}\0"));

        var candidate = Assert.Single((await AesKeyScanner.ScanAsync(path)).Candidates);

        Assert.Equal("0x" + Convert.ToHexString(key), candidate.Key);
        Assert.Equal(AesKeyOrigin.LabelledBase64, candidate.Origin);
    }

    [Fact]
    public async Task Finds_key_split_across_streaming_chunk_boundary()
    {
        const int chunkSize = 4 * 1024 * 1024;
        var bytes = new byte[chunkSize + 256];
        Array.Fill(bytes, (byte)'!');
        Encoding.ASCII.GetBytes("0x" + Hex).CopyTo(bytes, chunkSize - 20);
        var path = Write("boundary.bin", bytes);

        var candidate = Assert.Single((await AesKeyScanner.ScanAsync(path)).Candidates);

        Assert.Equal("0x" + Hex, candidate.Key);
        Assert.Equal(chunkSize - 18, candidate.Offset);
    }

    [Fact]
    public async Task Reconstructs_eight_consecutive_x64_stack_immediates()
    {
        var key = Enumerable.Range(0, 32).Select(i => (byte)(i + 1)).ToArray();
        var code = new List<byte>();
        for (var part = 0; part < 8; part++)
        {
            code.AddRange([0xC7, 0x44, 0x24, (byte)(0x20 + part * 4)]);
            code.AddRange(key.Skip(part * 4).Take(4));
            code.Add(0x90);
        }
        var path = Write("code.bin", code.ToArray());

        var candidate = Assert.Single((await AesKeyScanner.ScanAsync(path)).Candidates);

        Assert.Equal("0x" + Convert.ToHexString(key), candidate.Key);
        Assert.Equal(AesKeyOrigin.X86ImmediateStores, candidate.Origin);
    }

    [Fact]
    public async Task Machine_code_profile_can_be_disabled()
    {
        var code = new byte[8 * 8];
        for (var part = 0; part < 8; part++)
        {
            var offset = part * 8;
            code[offset] = 0xC7;
            code[offset + 1] = 0x45;
            code[offset + 2] = unchecked((byte)(sbyte)(-32 + part * 4));
            BinaryPrimitives.WriteUInt32LittleEndian(code.AsSpan(offset + 3, 4), (uint)(part + 1));
            code[offset + 7] = 0x90;
        }

        var path = Write("disabled.bin", code);
        var result = await AesKeyScanner.ScanAsync(path, scanMachineCode: false);

        Assert.Empty(result.Candidates);
    }

    [Fact]
    public async Task Candidate_limit_is_enforced_and_reported()
    {
        var one = Hex;
        var two = "FEDCBA9876543210FEDCBA9876543210FEDCBA9876543210FEDCBA9876543210";
        var path = Write("limit.bin", Encoding.ASCII.GetBytes($"{one}\0{two}\0"));

        var result = await AesKeyScanner.ScanAsync(path, maxCandidates: 1);

        Assert.Single(result.Candidates);
        Assert.True(result.Truncated);
    }

    private string Write(string name, byte[] bytes)
    {
        var path = _tmp.File(name);
        File.WriteAllBytes(path, bytes);
        return path;
    }
}

public sealed class KeyDiscoveryCliTests : IDisposable
{
    private readonly TempDir _tmp = new();
    public void Dispose() => _tmp.Dispose();

    [Fact]
    public void Parses_safe_offline_scan_options()
    {
        var source = _tmp.File("game.exe");

        var options = KeyDiscoveryCli.Parse([
            "keys", "scan", "--source", source, "--game", "4.27", "--max-candidates", "12",
            "--no-code-patterns", "--show-keys", "--json"]);

        Assert.NotNull(options);
        Assert.Equal(source, options.SourcePath);
        Assert.Equal(12, options.MaxCandidates);
        Assert.False(options.ScanMachineCode);
        Assert.True(options.ShowKeys);
        Assert.True(options.Json);
        Assert.Equal(CUE4Parse.UE4.Versions.EGame.GAME_UE4_27, options.Game);
    }

    [Fact]
    public void Verified_only_requires_containers()
    {
        var source = _tmp.File("game.exe");

        var error = Assert.Throws<UserFacingException>(() =>
            KeyDiscoveryCli.Parse(["keys", "scan", "--source", source, "--verified-only"]));

        Assert.Contains("requires --paks", error.Headline);
    }
}
