using System.Buffers.Binary;
using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UEBulkExport;

public enum ManagedSourceSymbols { EmbeddedPortablePdb, SidecarPortablePdb, Missing }
public enum ManagedSourceState { Verified, NoEmbeddedSource, MissingChecksum, InvalidSource, LimitReached }

public sealed record ManagedSourceDocument(string Name, ManagedSourceState State,
    string? ChecksumAlgorithm = null, string? Checksum = null, string? OutputFile = null);

public sealed record ManagedSourceReport(int Version, string Assembly, ManagedSourceSymbols Symbols,
    IReadOnlyList<ManagedSourceDocument> Documents, bool DocumentLimitReached, string? OutputDirectory)
{
    public int Verified => Documents.Count(document => document.State == ManagedSourceState.Verified);
}

/// <summary>Reads original C# documents carried by a managed assembly's Portable PDB without loading the assembly.</summary>
public static class ManagedSourceRecovery
{
    private static readonly Guid EmbeddedSource = new("0E8A571B-6926-466E-B4AD-8AB04611F5FE");
    private static readonly Guid CSharp = new("3F5162F8-07C6-11D3-9053-00C04FA302A1");
    private static readonly Guid Sha1 = new("FF1816EC-AA5E-4D10-87F7-6F4963833460");
    private static readonly Guid Sha256 = new("8829D00F-11B8-4213-878B-770E8597AC16");
    private const int MaxPdbBytes = 128 * 1024 * 1024;
    private const int MaxDocumentBytes = 16 * 1024 * 1024;
    private const int MaxRecoveredBytes = 128 * 1024 * 1024;
    private const int MaxDocuments = 5000;
    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static ManagedSourceReport Run(string assemblyPath, string? pdbPath = null,
        string? outputParent = null, CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var path = Path.GetFullPath(assemblyPath);
            using var assembly = new PEReader(File.OpenRead(path));
            if (!assembly.HasMetadata)
                throw new UserFacingException("The input does not contain managed .NET metadata.",
                    "Choose a managed C# assembly. Native and Native AOT binaries carry different data.");

            var (symbols, readerProvider) = OpenSymbols(assembly, path, pdbPath);
            using var provider = readerProvider;
            return provider is null ? new(1, Path.GetFileName(path), symbols, [], false, null) :
                ReadDocuments(Path.GetFileName(path), symbols, provider.GetMetadataReader(), outputParent, ct);
        }
        catch (Exception e) when (e is BadImageFormatException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new UserFacingException("The assembly, Portable PDB or output could not be read or written.",
                "Check the input format, output path and file permissions.");
        }
    }

    private static (ManagedSourceSymbols, MetadataReaderProvider?) OpenSymbols(PEReader assembly, string path, string? pdbPath)
    {
        var debug = assembly.ReadDebugDirectory();
        if (pdbPath is null)
        {
            var embedded = debug.FirstOrDefault(entry => entry.Type == DebugDirectoryEntryType.EmbeddedPortablePdb);
            if (embedded.Type == DebugDirectoryEntryType.EmbeddedPortablePdb)
            {
                var header = assembly.GetSectionData(embedded.DataRelativeVirtualAddress).GetReader();
                if (header.RemainingBytes < 8 || header.ReadUInt32() != 0x4244504D ||
                    header.ReadInt32() is < 1 or > MaxPdbBytes)
                    throw new BadImageFormatException("Embedded Portable PDB size is outside the supported limit.");
                return (ManagedSourceSymbols.EmbeddedPortablePdb,
                    assembly.ReadEmbeddedPortablePdbDebugDirectoryData(embedded));
            }

            pdbPath = Path.ChangeExtension(path, ".pdb");
            if (!File.Exists(pdbPath)) return (ManagedSourceSymbols.Missing, null);
        }

        var stream = File.OpenRead(pdbPath);
        MetadataReaderProvider? provider = null;
        try
        {
            if (stream.Length > MaxPdbBytes) throw new BadImageFormatException("Portable PDB is too large.");
            provider = MetadataReaderProvider.FromPortablePdbStream(stream, MetadataStreamOptions.PrefetchMetadata);
            var header = provider.GetMetadataReader().DebugMetadataHeader ??
                throw new BadImageFormatException("The input is missing a Portable PDB header.");
            var id = new BlobContentId(header.Id);
            if (!debug.Any(entry => MatchesPdb(assembly, entry, id)))
                throw new UserFacingException("The Portable PDB does not match this assembly.",
                    "Use the symbols produced by the same build.");

            return (ManagedSourceSymbols.SidecarPortablePdb, provider);
        }
        catch
        {
            provider?.Dispose();
            stream.Dispose();
            throw;
        }
    }

    private static bool MatchesPdb(PEReader assembly, DebugDirectoryEntry entry, BlobContentId id)
    {
        if (!entry.IsPortableCodeView || entry.Stamp != id.Stamp) return false;
        var codeView = assembly.ReadCodeViewDebugDirectoryData(entry);
        return codeView.Age == 1 && codeView.Guid == id.Guid;
    }

    private static ManagedSourceReport ReadDocuments(string assembly, ManagedSourceSymbols symbols, MetadataReader reader,
        string? outputParent, CancellationToken ct)
    {
        var documents = new List<ManagedSourceDocument>();
        var remaining = MaxRecoveredBytes;
        string? output = null;
        string? staging = null;
        var truncated = false;
        try
        {
            foreach (var handle in reader.Documents)
            {
                ct.ThrowIfCancellationRequested();
                var document = reader.GetDocument(handle);
                if (reader.GetGuid(document.Language) != CSharp) continue;
                if (documents.Count == MaxDocuments)
                {
                    truncated = true;
                    break;
                }
                var name = FileName(reader.GetString(document.Name));
                var original = ReadDocument(reader, handle, document, name, ref remaining, out var proof);
                if (original is not null && outputParent is not null)
                {
                    if (staging is null)
                    {
                        output = Path.Combine(Path.GetFullPath(outputParent), "sources-" + Guid.NewGuid().ToString("N"));
                        staging = output + ".partial";
                        Directory.CreateDirectory(staging);
                    }
                    var relative = $"{documents.Count + 1:D4}-{name}";
                    AtomicFile.WriteAllBytes(Path.Combine(staging, relative), original);
                    proof = proof with { OutputFile = relative };
                }
                documents.Add(proof);
            }
            var report = new ManagedSourceReport(1, assembly, symbols, documents.AsReadOnly(), truncated, output);
            if (staging is not null)
            {
                ct.ThrowIfCancellationRequested();
                AtomicFile.WriteAllBytes(Path.Combine(staging, "managed-source-report.json"),
                    JsonSerializer.SerializeToUtf8Bytes(report with { OutputDirectory = null }, Json));
                Directory.Move(staging, output!);
                staging = null;
            }
            return report;
        }
        finally
        {
            if (staging is not null && Directory.Exists(staging))
            {
                try { Directory.Delete(staging, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static byte[]? ReadDocument(MetadataReader reader, DocumentHandle handle, Document document,
        string name, ref int remaining, out ManagedSourceDocument proof)
    {
        foreach (var infoHandle in reader.GetCustomDebugInformation(handle))
        {
            var info = reader.GetCustomDebugInformation(infoHandle);
            if (reader.GetGuid(info.Kind) != EmbeddedSource) continue;
            var algorithm = reader.GetGuid(document.HashAlgorithm);
            if (document.Hash.IsNil || (algorithm != Sha1 && algorithm != Sha256))
            {
                proof = new(name, ManagedSourceState.MissingChecksum);
                return null;
            }

            var blob = reader.GetBlobReader(info.Value);
            var format = blob.RemainingBytes >= sizeof(int) ? blob.ReadInt32() : -1;
            if (format < 0)
            {
                proof = new(name, ManagedSourceState.InvalidSource);
                return null;
            }
            var length = format == 0 ? blob.RemainingBytes : format;
            if (length > MaxDocumentBytes || length > remaining || blob.RemainingBytes > MaxDocumentBytes)
            {
                proof = new(name, ManagedSourceState.LimitReached);
                return null;
            }

            remaining -= length;
            var original = Decode(format, blob.ReadBytes(blob.RemainingBytes));
            byte[]? actual = original is null ? null : algorithm == Sha1 ? SHA1.HashData(original) : SHA256.HashData(original);
            if (actual is null || !actual.AsSpan().SequenceEqual(reader.GetBlobBytes(document.Hash)))
            {
                proof = new(name, ManagedSourceState.InvalidSource);
                return null;
            }

            proof = new(name, ManagedSourceState.Verified, algorithm == Sha1 ? "SHA-1" : "SHA-256", Convert.ToHexStringLower(actual));
            return original;
        }

        proof = new(name, ManagedSourceState.NoEmbeddedSource);
        return null;
    }

    internal static byte[]? Decode(int format, byte[] content)
    {
        if (format == 0) return content;
        if (format is < 1 or > MaxDocumentBytes) return null;
        var data = new byte[format];
        try
        {
            using var stream = new DeflateStream(new MemoryStream(content, writable: false), CompressionMode.Decompress);
            stream.ReadExactly(data);
            return stream.ReadByte() == -1 ? data : null;
        }
        catch (Exception e) when (e is EndOfStreamException or InvalidDataException) { return null; }
    }

    internal static string FileName(string path)
    {
        var start = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\')) + 1;
        var leaf = new string(path[start..].Take(100)
            .Select(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '_').ToArray()).Trim('.');
        if (leaf.Length == 0) return "source.cs";
        return leaf.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ? leaf : leaf + ".cs";
    }
}
