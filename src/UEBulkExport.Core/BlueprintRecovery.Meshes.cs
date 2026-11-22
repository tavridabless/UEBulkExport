using System.Buffers.Binary;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse_Conversion.Formats.Meshes;
using CUE4Parse_Conversion.Options;

namespace UEBulkExport;

internal sealed record BlueprintMeshSocket(string Name, BlueprintComponentVector RelativeLocation,
    BlueprintComponentRotation RelativeRotation, BlueprintComponentVector RelativeScale, string? Tag = null);

internal sealed record BlueprintExtractedMesh(string SourceObjectPath, string GeometryPath,
    IReadOnlyList<BlueprintMeshSocket> Sockets)
{
    public IReadOnlyList<BlueprintMeshMaterialSlot> MaterialSlots { get; init; } = [];
    public IReadOnlyList<BlueprintMeshLod> Lods { get; init; } = [];
    public IReadOnlyList<BlueprintMeshBox> Boxes { get; init; } = [];
    public IReadOnlyList<BlueprintMeshSphere> Spheres { get; init; } = [];
    public IReadOnlyList<BlueprintMeshCapsule> Capsules { get; init; } = [];
}

internal sealed record BlueprintMeshExtractionIssue(string SourceObjectPath, string Message);

internal sealed record BlueprintMeshExtractionResult(IReadOnlyList<BlueprintExtractedMesh> Meshes,
    IReadOnlyList<BlueprintMeshExtractionIssue> Issues)
{
    public IReadOnlyList<BlueprintExtractedMaterial> Materials { get; init; } = [];
    public IReadOnlyList<BlueprintExtractedTexture> Textures { get; init; } = [];
    public IReadOnlyList<BlueprintMeshExtractionIssue> Warnings { get; init; } = [];
    public bool HasGaps => Issues.Count > 0 || Warnings.Count > 0;
}

public sealed partial class BulkExporter
{
    private const long MaxBlueprintMeshSourceBytes = 256L * 1024 * 1024;
    private const int MaxBlueprintMeshPackageExports = 4_096;

    /// <summary>Exports conventional LODs, sockets and supported simple collision of exact mounted meshes.</summary>
    internal Task<BlueprintMeshExtractionResult> ExtractBlueprintMeshesAsync(IReadOnlyCollection<string> objectPaths,
        string outputDirectory, CancellationToken cancellationToken = default) =>
        Task.Run(() => ExtractBlueprintMeshes(objectPaths, Path.GetFullPath(outputDirectory), cancellationToken),
            cancellationToken);

    private BlueprintMeshExtractionResult ExtractBlueprintMeshes(IReadOnlyCollection<string> objectPaths,
        string outputDirectory, CancellationToken ct)
    {
        var extracted = new List<BlueprintExtractedMesh>();
        var issues = new List<BlueprintMeshExtractionIssue>();
        var warnings = new List<BlueprintMeshExtractionIssue>();
        var surfaces = new BlueprintSurfaceExtraction(this, outputDirectory, warnings, ct);
        var options = new ExportOptions(meshFormat: EMeshFormat.Gltf2, meshQuality: EMeshQuality.All,
            texturePlatform: _options.Platform, exportMaterials: false, socketFormat: ESocketFormat.None);
        var format = new GltfMeshFormat();
        foreach (var objectPath in objectPaths.Distinct(StringComparer.Ordinal).OrderBy(path => path, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (!BlueprintRecovery.IsStaticMeshObjectPath(objectPath))
                    throw new UserFacingException("The mesh reference is not an exact /Game or /Engine object path.");
                var separator = objectPath.IndexOf('.');
                var packagePath = objectPath[..separator];
                var objectName = objectPath[(separator + 1)..];
                var file = ResolveBlueprintMeshPackage(packagePath);
                if (file is null)
                    throw new UserFacingException("The exact mesh package is absent from the mounted containers.");
                if (!IsWithinBlueprintMeshSourceBudget(file))
                    throw new UserFacingException("The mesh package and its payloads have an unknown size or exceed 256 MiB.");

                var package = _provider.LoadPackage(file);
                if (package is IoPackage && !string.Equals(package.Name, packagePath, StringComparison.OrdinalIgnoreCase))
                    throw new UserFacingException("The mounted IoStore package name differs from the referenced mesh package.");
                if (!package.IsFullyLoaded || package.ExportMapLength > MaxBlueprintMeshPackageExports)
                    throw new UserFacingException("The mesh package export table is incomplete or exceeds 4096 objects.");
                var export = BlueprintRecoveryScan.FindTopLevelAssetExport(package, objectName,
                    static type => type == "/Script/Engine.StaticMesh", ct);
                if (export?.Load<UStaticMesh>() is not { } mesh)
                    throw new UserFacingException("The package does not contain one exact top-level native StaticMesh export with this name.");
                if (mesh.RenderData?.LODs?.Any(lod => !lod.SkipLod) != true)
                    throw new UserFacingException("The mesh has no readable conventional render LOD.");

                var sockets = BlueprintRecoveryScan.ReadStaticMeshSockets(mesh, export, objectPath, issues, ct);
                var (destination, lods) = WriteBlueprintMeshLods(mesh, objectName, objectPath, outputDirectory, options, format, warnings, ct);
                var collision = BlueprintRecoveryScan.ReadBlueprintMeshCollision(mesh, export, objectPath, warnings, ct);
                extracted.Add(new BlueprintExtractedMesh(objectPath, destination, sockets)
                {
                    MaterialSlots = surfaces.ReadSlots(mesh, objectPath),
                    Lods = lods,
                    Boxes = collision.Boxes,
                    Spheres = collision.Spheres,
                    Capsules = collision.Capsules
                });
            }
            catch (Exception error) when (BlueprintRecoveryScan.IsReadFailure(error))
            {
                issues.Add(new BlueprintMeshExtractionIssue(objectPath, error.Message));
            }
        }
        ct.ThrowIfCancellationRequested();
        return new BlueprintMeshExtractionResult(extracted.AsReadOnly(), issues.AsReadOnly())
        {
            Materials = surfaces.Materials,
            Textures = surfaces.Textures,
            Warnings = warnings.AsReadOnly()
        };
    }

    private GameFile? ResolveBlueprintMeshPackage(string packagePath)
    {
        // CUE4Parse's general path fixer also treats Content/Config/Plugins as path
        // aliases. Here their names inside a canonical package path are literal folders.
        var physical = BlueprintMeshContainerPath(packagePath, _provider.ProjectName);
        if (_provider.Files.TryGetValue(physical, out var file) ||
            _provider.Files.TryGetValue(packagePath[1..] + ".uasset", out file) ||
            _provider.Files.TryGetValue(packagePath + ".uasset", out file))
            return file;
        return null;
    }

    internal static string BlueprintMeshContainerPath(string packagePath, string projectName)
    {
        var engine = packagePath.StartsWith("/Engine/", StringComparison.Ordinal);
        var root = engine ? "Engine" : projectName;
        var relative = packagePath[(engine ? "/Engine/".Length : "/Game/".Length)..];
        return $"{root}/Content/{relative}.uasset";
    }

    private bool IsWithinBlueprintMeshSourceBudget(GameFile file)
    {
        _provider.Files.FindPayloads(file, out var export, out var bulk, out var optional);
        var remaining = MaxBlueprintMeshSourceBytes;
        if (!Reserve(file) || export is not null && !Reserve(export)) return false;
        return bulk.All(Reserve) && optional.All(Reserve);

        bool Reserve(GameFile payload)
        {
            if (payload.Size < 0 || payload.Size > remaining) return false;
            remaining -= payload.Size;
            return true;
        }
    }

    internal static bool IsBinaryGltf(ReadOnlySpan<byte> data)
    {
        if (data.Length < 36 || !data[..4].SequenceEqual("glTF"u8) ||
            BinaryPrimitives.ReadUInt32LittleEndian(data[4..]) != 2 ||
            BinaryPrimitives.ReadUInt32LittleEndian(data[8..]) != data.Length ||
            !data.Slice(16, 4).SequenceEqual("JSON"u8))
            return false;
        var jsonLength = BinaryPrimitives.ReadUInt32LittleEndian(data[12..]);
        if (jsonLength is < 4 || jsonLength % 4 != 0 || jsonLength > data.Length - 32) return false;
        var binary = data[(20 + (int)jsonLength)..];
        return binary.Slice(4, 4).SequenceEqual("BIN\0"u8) &&
               BinaryPrimitives.ReadUInt32LittleEndian(binary) == binary.Length - 8;
    }
}

internal sealed partial class BlueprintRecoveryScan
{
    private const int MaxBlueprintMeshSockets = 512;

    internal static ResolvedObject? FindTopLevelAssetExport(IPackage package, string name, Func<string?, bool> supportsClass,
        CancellationToken ct)
    {
        ResolvedObject? selected = null;
        for (var index = 0; index < package.ExportMapLength; index++)
        {
            ct.ThrowIfCancellationRequested();
            var export = package.ResolvePackageIndex(new FPackageIndex(package, index + 1));
            if (export is not { Outer: ResolvedPackageObject outer } || !ReferenceEquals(outer.Package, package) ||
                !export.Name.Text.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                !supportsClass(ObjectPath(export.Class)))
                continue;
            if (selected is not null) return null;
            selected = export;
        }
        return selected;
    }

    internal static IReadOnlyList<BlueprintMeshSocket> ReadStaticMeshSockets(UStaticMesh mesh, ResolvedObject meshExport,
        string objectPath, List<BlueprintMeshExtractionIssue> issues, CancellationToken ct)
    {
        var sockets = new List<BlueprintMeshSocket>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var repeated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var references = mesh.Sockets ?? [];
        for (var index = 0; index < Math.Min(references.Length, MaxBlueprintMeshSockets); index++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var export = ReadLocalComponentExport(references[index], meshExport, "StaticMeshSocket");
                if (export?.Load<UStaticMeshSocket>() is not { } socket ||
                    !IsRestorableStaticMeshSocketName(socket.SocketName.Text) ||
                    Vector(socket.RelativeLocation) is not { } location || Rotation(socket.RelativeRotation) is not { } rotation ||
                    Vector(socket.RelativeScale) is not { } scale)
                {
                    Issue($"Socket {index + 1} is not a bounded local StaticMeshSocket with a readable name and finite transform.");
                    continue;
                }
                if (!names.Add(socket.SocketName.Text))
                {
                    if (repeated.Add(socket.SocketName.Text)) Issue($"Socket {socket.SocketName.Text} is ambiguous.");
                    continue;
                }
                var tag = socket.Tag;
                if (tag is { Length: > MaxTextLength })
                {
                    Issue($"Socket {socket.SocketName.Text} has a tag above the supported length.");
                    tag = null;
                }
                sockets.Add(new BlueprintMeshSocket(socket.SocketName.Text, location, rotation, scale, tag));
            }
            catch (Exception error) when (IsReadFailure(error))
            {
                Issue($"Socket {index + 1}: {Clip(error.Message)}");
            }
        }
        if (references.Length > MaxBlueprintMeshSockets) Issue("The mesh socket limit (512) was reached.");
        return sockets.Where(socket => !repeated.Contains(socket.Name)).ToArray();

        void Issue(string message) => issues.Add(new BlueprintMeshExtractionIssue(objectPath, message));
    }

    internal static bool IsRestorableStaticMeshSocketName(string name) =>
        name.Length is > 0 and <= 128 && !string.IsNullOrWhiteSpace(name) &&
        !name.Equals("None", StringComparison.OrdinalIgnoreCase) && !name.Any(char.IsControl);
}
