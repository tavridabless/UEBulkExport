using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace UEBulkExport;

public enum AesKeyOrigin
{
    AsciiHex,
    Utf16Hex,
    LabelledBase64,
    X86ImmediateStores,
    CooperativeProbe
}

/// <summary>A potential 256-bit Unreal container key found in an explicitly selected file.</summary>
public sealed record AesKeyCandidate(
    string Key,
    long Offset,
    AesKeyOrigin Origin,
    string Evidence,
    int Confidence,
    string? ContainerGuid = null)
{
    /// <summary>A stable identifier suitable for logs that does not disclose any key bytes.</summary>
    public string Fingerprint
    {
        get
        {
            var bytes = Convert.FromHexString(Key.AsSpan(2));
            return Convert.ToHexString(SHA256.HashData(bytes).AsSpan(0, 6));
        }
    }
}

public sealed record AesKeyScanResult(
    IReadOnlyList<AesKeyCandidate> Candidates,
    long BytesScanned,
    bool Truncated);

/// <summary>
/// Bounded, streaming scanner for executables and user-created dump files. It deliberately has
/// no process-opening or injection API: acquisition and analysis remain separate trust boundaries.
/// </summary>
public static class AesKeyScanner
{
    private const int ChunkSize = 4 * 1024 * 1024;
    private const int Overlap = 512;

    public static async Task<AesKeyScanResult> ScanAsync(
        string sourcePath,
        int maxCandidates = 256,
        bool scanMachineCode = true,
        CancellationToken ct = default)
    {
        if (maxCandidates is < 1 or > 4096)
            throw new UserFacingException("The AES candidate limit must be between 1 and 4096.");

        var path = Path.GetFullPath(sourcePath.Trim().Trim('"'));
        if (!File.Exists(path))
            throw new UserFacingException($"Key source file not found: {path}",
                "Select a game executable, DLL, Crypto.json, or a dump file you created.");

        var candidates = new Dictionary<string, AesKeyCandidate>(StringComparer.OrdinalIgnoreCase);
        var buffer = ArrayPool<byte>.Shared.Rent(ChunkSize + Overlap);
        long position = 0;
        var truncated = false;

        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                ChunkSize, FileOptions.Asynchronous | FileOptions.SequentialScan);

            while (position < stream.Length)
            {
                ct.ThrowIfCancellationRequested();
                stream.Position = position;

                var wanted = (int)Math.Min(buffer.Length, stream.Length - position);
                var read = 0;
                while (read < wanted)
                {
                    var n = await stream.ReadAsync(buffer.AsMemory(read, wanted - read), ct);
                    if (n == 0) break;
                    read += n;
                }

                if (read == 0) break;

                var primaryLength = Math.Min(ChunkSize, read);
                var data = buffer.AsSpan(0, read);
                ScanAsciiHex(data, primaryLength, position, candidates, maxCandidates);
                ScanUtf16Hex(data, primaryLength, position, candidates, maxCandidates);
                ScanLabelledBase64(data, primaryLength, position, candidates, maxCandidates);
                if (scanMachineCode)
                    ScanImmediateStores(data, primaryLength, position, candidates, maxCandidates);

                if (candidates.Count >= maxCandidates)
                {
                    // Reaching the cap means at least one scanner stopped accepting evidence in
                    // this chunk; conservatively tell the caller that the result set is bounded.
                    truncated = true;
                    break;
                }

                position += primaryLength;
            }

            var ordered = candidates.Values
                .OrderByDescending(c => c.Confidence)
                .ThenBy(c => c.Offset)
                .ToList();
            return new AesKeyScanResult(ordered, Math.Min(position + ChunkSize, stream.Length), truncated);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new UserFacingException($"Cannot read key source: {path}", e.Message);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void ScanAsciiHex(
        ReadOnlySpan<byte> data,
        int primaryLength,
        long baseOffset,
        Dictionary<string, AesKeyCandidate> output,
        int limit)
    {
        Span<byte> key = stackalloc byte[32];
        for (var start = 0; start + 64 <= data.Length && output.Count < limit; start++)
        {
            if (start >= primaryLength) break;
            if (start > 0 && IsHex(data[start - 1])) continue;

            var prefix = start + 66 <= data.Length && data[start] == (byte)'0' &&
                         (data[start + 1] == (byte)'x' || data[start + 1] == (byte)'X');
            var keyStart = prefix ? start + 2 : start;
            if (keyStart + 64 > data.Length || !AllHex(data.Slice(keyStart, 64))) continue;
            if (keyStart + 64 < data.Length && IsHex(data[keyStart + 64])) continue;

            DecodeHex(data.Slice(keyStart, 64), 1, key);
            Add(output, key, baseOffset + keyStart, AesKeyOrigin.AsciiHex,
                prefix ? "0x-prefixed 256-bit hexadecimal value" : "256-bit hexadecimal value",
                prefix ? 92 : 82, limit);
            start = keyStart + 63;
        }
    }

    private static void ScanUtf16Hex(
        ReadOnlySpan<byte> data,
        int primaryLength,
        long baseOffset,
        Dictionary<string, AesKeyCandidate> output,
        int limit)
    {
        const int encodedLength = 64 * 2;
        Span<byte> key = stackalloc byte[32];
        for (var start = 0; start + encodedLength <= data.Length && output.Count < limit; start++)
        {
            if (start >= primaryLength) break;
            if (start >= 2 && data[start - 1] == 0 && IsHex(data[start - 2])) continue;

            var prefix = start + encodedLength + 4 <= data.Length &&
                         data[start] == (byte)'0' && data[start + 1] == 0 &&
                         (data[start + 2] == (byte)'x' || data[start + 2] == (byte)'X') && data[start + 3] == 0;
            var keyStart = prefix ? start + 4 : start;
            if (keyStart + encodedLength > data.Length) continue;

            var valid = true;
            for (var i = 0; i < 64; i++)
            {
                if (!IsHex(data[keyStart + i * 2]) || data[keyStart + i * 2 + 1] != 0)
                {
                    valid = false;
                    break;
                }
            }

            if (!valid) continue;
            if (keyStart + encodedLength + 1 < data.Length && data[keyStart + encodedLength + 1] == 0 &&
                IsHex(data[keyStart + encodedLength])) continue;

            DecodeHex(data.Slice(keyStart, encodedLength), 2, key);
            Add(output, key, baseOffset + keyStart, AesKeyOrigin.Utf16Hex,
                prefix ? "UTF-16 0x-prefixed 256-bit hexadecimal value" : "UTF-16 256-bit hexadecimal value",
                prefix ? 94 : 84, limit);
            start = keyStart + encodedLength - 1;
        }
    }

    private static void ScanLabelledBase64(
        ReadOnlySpan<byte> data,
        int primaryLength,
        long baseOffset,
        Dictionary<string, AesKeyCandidate> output,
        int limit)
    {
        const int encodedLength = 44;
        Span<byte> key = stackalloc byte[32];
        Span<char> chars = stackalloc char[encodedLength];
        for (var start = 0; start + encodedLength <= data.Length && output.Count < limit; start++)
        {
            if (start >= primaryLength) break;
            if (start > 0 && IsBase64(data[start - 1])) continue;

            var encoded = data.Slice(start, encodedLength);
            if (!IsPlausibleBase64(encoded) ||
                (start + encodedLength < data.Length && IsBase64(data[start + encodedLength]))) continue;

            var contextStart = Math.Max(0, start - 96);
            var contextLength = Math.Min(data.Length - contextStart, encodedLength + 128);
            var context = Encoding.ASCII.GetString(data.Slice(contextStart, contextLength));
            if (!context.Contains("aes", StringComparison.OrdinalIgnoreCase) &&
                !context.Contains("encryption", StringComparison.OrdinalIgnoreCase) &&
                !context.Contains("crypto", StringComparison.OrdinalIgnoreCase)) continue;

            for (var i = 0; i < encodedLength; i++) chars[i] = (char)encoded[i];
            if (!Convert.TryFromBase64Chars(chars, key, out var written) || written != key.Length) continue;

            Add(output, key, baseOffset + start, AesKeyOrigin.LabelledBase64,
                "labelled Base64 value decoding to 256 bits", 86, limit);
            start += encodedLength - 1;
        }
    }

    private readonly record struct ImmediateStore(int Offset, byte BaseRegister, int Displacement, uint Value);

    /// <summary>
    /// Clean-room recognition of common compiler output: eight immediate 32-bit writes to
    /// consecutive stack slots. It is intentionally architecture-local and never executes code.
    /// </summary>
    private static void ScanImmediateStores(
        ReadOnlySpan<byte> data,
        int primaryLength,
        long baseOffset,
        Dictionary<string, AesKeyCandidate> output,
        int limit)
    {
        var stores = new List<ImmediateStore>();
        for (var i = 0; i + 7 <= data.Length; i++)
        {
            if (data[i] != 0xC7) continue;

            if (data[i + 1] == 0x45 && i + 7 <= data.Length)
            {
                stores.Add(new ImmediateStore(i, 0, unchecked((sbyte)data[i + 2]),
                    BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(i + 3, 4))));
            }
            else if (data[i + 1] == 0x85 && i + 10 <= data.Length)
            {
                stores.Add(new ImmediateStore(i, 0, BinaryPrimitives.ReadInt32LittleEndian(data.Slice(i + 2, 4)),
                    BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(i + 6, 4))));
            }
            else if (data[i + 1] == 0x44 && data[i + 2] == 0x24 && i + 8 <= data.Length)
            {
                stores.Add(new ImmediateStore(i, 1, unchecked((sbyte)data[i + 3]),
                    BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(i + 4, 4))));
            }
            else if (data[i + 1] == 0x84 && data[i + 2] == 0x24 && i + 11 <= data.Length)
            {
                stores.Add(new ImmediateStore(i, 1, BinaryPrimitives.ReadInt32LittleEndian(data.Slice(i + 3, 4)),
                    BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(i + 7, 4))));
            }
        }

        Span<byte> key = stackalloc byte[32];
        for (var first = 0; first < stores.Count && output.Count < limit; first++)
        {
            var anchor = stores[first];
            if (anchor.Offset >= primaryLength) break;

            var window = stores.Skip(first)
                .TakeWhile(s => s.Offset - anchor.Offset <= 192)
                .Where(s => s.BaseRegister == anchor.BaseRegister)
                .GroupBy(s => s.Displacement)
                .ToDictionary(g => g.Key, g => g.First());

            foreach (var displacement in window.Keys.OrderBy(d => d))
            {
                var complete = true;
                var firstOffset = int.MaxValue;
                var lastOffset = int.MinValue;
                for (var part = 0; part < 8; part++)
                {
                    if (!window.TryGetValue(displacement + part * 4, out var store))
                    {
                        complete = false;
                        break;
                    }

                    BinaryPrimitives.WriteUInt32LittleEndian(key.Slice(part * 4, 4), store.Value);
                    firstOffset = Math.Min(firstOffset, store.Offset);
                    lastOffset = Math.Max(lastOffset, store.Offset);
                }

                if (!complete || lastOffset - firstOffset > 192 || CountDistinct(key) < 4) continue;
                Add(output, key, baseOffset + firstOffset, AesKeyOrigin.X86ImmediateStores,
                    "eight immediate 32-bit writes to consecutive x86/x64 stack slots", 58, limit);
            }
        }
    }

    private static void Add(
        Dictionary<string, AesKeyCandidate> output,
        ReadOnlySpan<byte> bytes,
        long offset,
        AesKeyOrigin origin,
        string evidence,
        int confidence,
        int limit)
    {
        // The all-zero key is the provider's "no encryption" sentinel, not a discovered secret.
        // Do not reject other low-entropy values: weak keys are still valid AES-256 keys.
        if (output.Count >= limit || bytes.IndexOfAnyExcept((byte)0) < 0) return;

        var key = "0x" + Convert.ToHexString(bytes);
        var candidate = new AesKeyCandidate(key, offset, origin, evidence, confidence);
        if (!output.TryGetValue(key, out var previous) || candidate.Confidence > previous.Confidence)
            output[key] = candidate;
    }

    private static bool AllHex(ReadOnlySpan<byte> value)
    {
        foreach (var b in value)
            if (!IsHex(b)) return false;
        return true;
    }

    private static bool IsHex(byte b) =>
        b is >= (byte)'0' and <= (byte)'9' or >= (byte)'A' and <= (byte)'F' or >= (byte)'a' and <= (byte)'f';

    private static bool IsBase64(byte b) =>
        b is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z' or >= (byte)'0' and <= (byte)'9' or
            (byte)'+' or (byte)'/' or (byte)'=';

    private static bool IsPlausibleBase64(ReadOnlySpan<byte> value)
    {
        if (value.Length != 44 || value[^1] != (byte)'=') return false;
        for (var i = 0; i < value.Length - 1; i++)
            if (!IsBase64(value[i]) || value[i] == (byte)'=') return false;
        return true;
    }

    private static void DecodeHex(ReadOnlySpan<byte> encoded, int stride, Span<byte> destination)
    {
        for (var i = 0; i < destination.Length; i++)
            destination[i] = (byte)((Nibble(encoded[i * 2 * stride]) << 4) | Nibble(encoded[(i * 2 + 1) * stride]));
    }

    private static int CountDistinct(ReadOnlySpan<byte> value)
    {
        Span<bool> seen = stackalloc bool[256];
        var count = 0;
        foreach (var b in value)
        {
            if (seen[b]) continue;
            seen[b] = true;
            count++;
        }
        return count;
    }

    private static int Nibble(byte value) => value switch
    {
        >= (byte)'0' and <= (byte)'9' => value - '0',
        >= (byte)'A' and <= (byte)'F' => value - 'A' + 10,
        _ => value - 'a' + 10
    };
}
