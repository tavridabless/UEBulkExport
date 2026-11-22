using System.Buffers.Binary;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse_Conversion.Options;
using CUE4Parse_Conversion.Textures;

namespace UEBulkExport;

internal sealed record BlueprintMeshMaterialSlot(int SlotIndex, string SlotName, string? MaterialObjectPath);
internal sealed record BlueprintMaterialColor(double R, double G, double B, double A);
internal sealed record BlueprintExtractedMaterial(string SourceObjectPath, string? BaseColorTexturePath,
    string? NormalTexturePath, string? RoughnessTexturePath, string? MetallicTexturePath, string? OcclusionTexturePath,
    BlueprintMaterialColor? BaseColor, double? Roughness, double? Metallic, double? Specular);
internal sealed record BlueprintExtractedTexture(string SourceObjectPath, string FilePath, bool Srgb,
    int Width, int Height, bool NormalMap);

public sealed partial class BulkExporter
{
    private sealed class BlueprintSurfaceExtraction(BulkExporter exporter, string outputDirectory,
        List<BlueprintMeshExtractionIssue> warnings, CancellationToken ct)
    {
        private const int MaxSlotsPerMesh = 128;
        private const int MaxSurfaceAssets = 256;
        private const int MaxParameters = 512;
        private const int MaxTextureDimension = 4096;
        private const long MaxTextureOutputBytes = 256L * 1024 * 1024;

        private readonly Dictionary<string, BlueprintExtractedMaterial?> _materials = new(StringComparer.Ordinal);
        private readonly Dictionary<string, BlueprintExtractedTexture?> _textures = new(StringComparer.Ordinal);
        private long _textureOutputBytes;

        public IReadOnlyList<BlueprintExtractedMaterial> Materials => _materials.Values.OfType<BlueprintExtractedMaterial>().ToArray();
        public IReadOnlyList<BlueprintExtractedTexture> Textures => _textures.Values.OfType<BlueprintExtractedTexture>().ToArray();

        public IReadOnlyList<BlueprintMeshMaterialSlot> ReadSlots(UStaticMesh mesh, string meshPath)
        {
            var result = new List<BlueprintMeshMaterialSlot>();
            var slots = mesh.StaticMaterials;
            if (slots.Length > MaxSlotsPerMesh) Warn(meshPath, "The material slot limit (128) was reached.");
            for (var index = 0; index < Math.Min(slots.Length, MaxSlotsPerMesh); index++)
            {
                ct.ThrowIfCancellationRequested();
                var slot = slots[index];
                var name = slot.MaterialSlotName.Text;
                if (slot.MaterialSlotName.IsNone || name.Length > 128 || name.Any(char.IsControl))
                {
                    if (!slot.MaterialSlotName.IsNone) Warn(meshPath, $"Material slot {index} has an unsupported name.");
                    name = $"MaterialSlot_{index}";
                }
                string? materialPath = null;
                try
                {
                    if (slot.MaterialInterface is { IsNull: false } reference)
                    {
                        materialPath = BlueprintRecoveryScan.ReadTopLevelAssetPath(reference, IsMaterialClass);
                        if (materialPath is null)
                            Warn(meshPath, $"Material slot {index} does not identify a supported top-level material asset.");
                        else if (materialPath.StartsWith("/Game/", StringComparison.Ordinal))
                            ReadMaterial(materialPath);
                    }
                    if (slot.OverlayMaterialInterface is { IsNull: false })
                        Warn(meshPath, $"Material slot {index} uses an overlay outside the reconstruction subset.");
                }
                catch (Exception error) when (BlueprintRecoveryScan.IsReadFailure(error))
                {
                    Warn(meshPath, $"Material slot {index}: {error.Message}");
                }
                result.Add(new BlueprintMeshMaterialSlot(index, name, materialPath));
            }
            return result;
        }

        private void ReadMaterial(string sourcePath)
        {
            if (_materials.ContainsKey(sourcePath)) return;
            if (_materials.Count >= MaxSurfaceAssets)
            {
                Warn(sourcePath, "The material asset limit (256) was reached.");
                return;
            }
            _materials.Add(sourcePath, null);
            try
            {
                var material = Load(sourcePath, IsMaterialClass)?.Load<UMaterialInterface>();
                var signals = material switch
                {
                    UMaterialInstanceConstant instance => ReadInstanceSignals(instance, sourcePath),
                    UMaterial direct => ReadCachedSignals(direct, sourcePath),
                    _ => throw new UserFacingException("The exact material asset could not be read from the mounted packages.")
                };

                var result = new BlueprintExtractedMaterial(sourcePath, Texture("baseColor"), Texture("normal"), Texture("roughness"),
                    Texture("metallic"), Texture("occlusion"), Color(), Scalar("roughness"), Scalar("metallic"), Scalar("specular"));
                if (result is { BaseColorTexturePath: null, NormalTexturePath: null, RoughnessTexturePath: null,
                        MetallicTexturePath: null, OcclusionTexturePath: null, BaseColor: null, Roughness: null,
                        Metallic: null, Specular: null })
                    Warn(sourcePath, "No supported named material parameters were recovered.");
                else _materials[sourcePath] = result;

                string? Texture(string channel)
                {
                    if (!signals.Textures.TryGetValue(channel, out var parameter)) return null;
                    var texturePath = parameter.Reference is { IsNull: false } reference
                        ? BlueprintRecoveryScan.ReadTopLevelAssetPath(reference, IsTextureClass)
                        : parameter.SoftPath;
                    if (texturePath is null)
                    {
                        Warn(sourcePath, $"The {channel} parameter does not identify an exact Texture2D asset.");
                        return null;
                    }
                    return ReadTexture(texturePath) ? texturePath : null;
                }

                double? Scalar(string channel)
                {
                    if (!signals.Scalars.TryGetValue(channel, out var value)) return null;
                    if (IsUnitValue(value)) return value;
                    Warn(sourcePath, $"The {channel} scalar is outside the supported 0–1 range.");
                    return null;
                }

                BlueprintMaterialColor? Color()
                {
                    if (!signals.Colors.TryGetValue("baseColor", out var value)) return null;
                    if (value is { } color && IsUnitValue(color.R) && IsUnitValue(color.G) &&
                        IsUnitValue(color.B) && IsUnitValue(color.A))
                        return new BlueprintMaterialColor(color.R, color.G, color.B, color.A);
                    Warn(sourcePath, "The baseColor vector contains unsupported values.");
                    return null;
                }
            }
            catch (Exception error) when (BlueprintRecoveryScan.IsReadFailure(error))
            {
                Warn(sourcePath, error.Message);
            }
        }

        private MaterialSignals ReadInstanceSignals(UMaterialInstanceConstant material, string sourcePath)
        {
            if (material.TextureParameterValues.Length > MaxParameters || material.ScalarParameterValues.Length > MaxParameters ||
                material.VectorParameterValues.Length > MaxParameters)
                throw new UserFacingException("A serialized material parameter list exceeds 512 entries.");
            if (material.bHasStaticPermutationResource || material.StaticParameters?.StaticSwitchParameters.Length > 0)
                Warn(sourcePath, "Static switch permutations are outside the editable material approximation.");
            if (material.BasePropertyOverrides is { } settings &&
                (settings.BlendMode != EBlendMode.BLEND_Opaque || settings.DitheredLODTransition))
                Warn(sourcePath, "Blend mode or dithered LOD overrides are outside the editable material approximation.");

            return new MaterialSignals(
                SelectInputs(material.TextureParameterValues.Select(parameter =>
                    (parameter.Name, (FMaterialParameterInfo?)parameter.ParameterInfo,
                     new TextureSignal(parameter.ParameterValue, null))), MaterialSignalKind.Texture, sourcePath),
                SelectInputs(material.ScalarParameterValues.Select(parameter =>
                    (parameter.Name, (FMaterialParameterInfo?)parameter.ParameterInfo, parameter.ParameterValue)),
                    MaterialSignalKind.Scalar, sourcePath),
                SelectInputs(material.VectorParameterValues.Select(parameter =>
                    (parameter.Name, parameter.ParameterInfo, parameter.ParameterValue)), MaterialSignalKind.Color, sourcePath));
        }

        private MaterialSignals ReadCachedSignals(UMaterial material, string sourcePath)
        {
            if (material.CachedExpressionData is not { } cache ||
                !cache.TryGetAllValues(out FStructFallback[] entries, "RuntimeEntries"))
                throw new UserFacingException("The cooked material lacks a supported named parameter cache.");
            if (material.TwoSided || material.bDisableDepthTest || material.bIsMasked || material.BlendMode != EBlendMode.BLEND_Opaque)
                Warn(sourcePath, "Two-sided, masked or non-opaque material properties are outside the editable approximation.");

            // Modern UE stores parallel global parameter keys and values in these typed cache
            // arrays. This recovers named inputs without following shader expressions.
            var textures = ReadCachedPairs<FSoftObjectPath>(cache, entries, 3, "TextureValues", sourcePath)
                .Select(parameter => (parameter.Name, parameter.Info, new TextureSignal(null, ReadSoftTexturePath(parameter.Value))));
            var scalars = ReadCachedPairs<float>(cache, entries, 0, "ScalarValues", sourcePath);
            var colors = ReadCachedPairs<FLinearColor>(cache, entries, 1, "VectorValues", sourcePath)
                .Select(parameter => (parameter.Name, parameter.Info, (FLinearColor?)parameter.Value));
            return new MaterialSignals(SelectInputs(textures, MaterialSignalKind.Texture, sourcePath),
                SelectInputs(scalars, MaterialSignalKind.Scalar, sourcePath), SelectInputs(colors, MaterialSignalKind.Color, sourcePath));
        }

        private IEnumerable<(string Name, FMaterialParameterInfo? Info, T Value)> ReadCachedPairs<T>(FStructFallback cache,
            FStructFallback[] entries, int entryIndex, string valueName, string sourcePath)
        {
            if (!cache.TryGetValue(out T[] values, valueName) || values.Length == 0) yield break;
            if (values.Length > MaxParameters || entryIndex >= entries.Length || entries[entryIndex] is not { } entry ||
                !entry.TryGetValue(out FMaterialParameterInfo[] names, "ParameterInfoSet") || names.Length != values.Length)
            {
                Warn(sourcePath, $"The {valueName} cache does not match a bounded parameter-name array.");
                yield break;
            }
            for (var index = 0; index < values.Length; index++)
            {
                ct.ThrowIfCancellationRequested();
                var name = names[index];
                if (name is not null) yield return (name.Name.Text, name, values[index]);
            }
        }

        private static string? ReadSoftTexturePath(FSoftObjectPath reference) =>
            string.IsNullOrEmpty(reference.SubPathString) && BlueprintRecovery.IsStaticMeshObjectPath(reference.AssetPathName.Text)
                ? reference.AssetPathName.Text : null;

        private sealed record MaterialSignals(Dictionary<string, TextureSignal> Textures,
            Dictionary<string, float> Scalars, Dictionary<string, FLinearColor?> Colors);

        private readonly record struct TextureSignal(FPackageIndex? Reference, string? SoftPath);

        private Dictionary<string, T> SelectInputs<T>(IEnumerable<(string Name, FMaterialParameterInfo? Info, T Value)> parameters,
            MaterialSignalKind kind, string sourcePath)
        {
            var result = new Dictionary<string, T>(StringComparer.Ordinal);
            var repeated = new HashSet<string>(StringComparer.Ordinal);
            var unsupported = 0;
            foreach (var parameter in parameters)
            {
                ct.ThrowIfCancellationRequested();
                var channel = MaterialSignalChannel(parameter.Name, kind);
                if (channel is null || parameter.Info is { Association: not EMaterialParameterAssociation.GlobalParameter })
                {
                    unsupported++;
                    continue;
                }
                if (!result.TryAdd(channel, parameter.Value) && repeated.Add(channel))
                    Warn(sourcePath, $"Several {kind.ToString().ToLowerInvariant()} parameters match {channel}.");
            }
            foreach (var channel in repeated) result.Remove(channel);
            if (unsupported > 0)
                Warn(sourcePath, $"{unsupported} {kind.ToString().ToLowerInvariant()} parameter(s) are outside the editable material subset.");
            return result;
        }

        private bool ReadTexture(string sourcePath)
        {
            if (_textures.TryGetValue(sourcePath, out var existing)) return existing is not null;
            if (_textures.Count >= MaxSurfaceAssets)
            {
                Warn(sourcePath, "The texture asset limit (256) was reached.");
                return false;
            }
            _textures.Add(sourcePath, null);
            try
            {
                if (Load(sourcePath, IsTextureClass)?.Load<UTexture2D>() is not { } texture ||
                    texture.PlatformData is not { Mips.Length: > 0 } data ||
                    data.VTData is { } virtualTexture && virtualTexture.IsInitialized())
                    throw new UserFacingException("The texture is not a readable, conventional Texture2D.");
                if (texture.IsHDR)
                    throw new UserFacingException("HDR textures cannot be preserved in the PNG material workflow.");
                CTexture? image = null;
                var mipIndex = 0;
                for (; mipIndex < data.Mips.Length; mipIndex++)
                {
                    ct.ThrowIfCancellationRequested();
                    var mip = data.Mips[mipIndex];
                    if (mip.SizeX is <= 0 or > MaxTextureDimension || mip.SizeY is <= 0 or > MaxTextureDimension || mip.SizeZ > 1)
                        continue;
                    var available = texture.GetMip(mipIndex);
                    if (available is null) continue;
                    image = texture.Decode(available, exporter._options.Platform);
                    if (image is not null) break;
                }
                if (image is null || PixelFormatUtils.IsHDR(image.PixelFormat))
                    throw new UserFacingException("No readable PNG-compatible mip within 4096×4096 was found.");
                if (mipIndex > 0) Warn(sourcePath, $"Texture recovery used mip {mipIndex} within the supported size and data limits.");

                var bytes = image.Encode(ETextureFormat.Png, saveHdrAsHdr: false, out var extension);
                if (extension != "png" || !HasPngDimensions(bytes, image.Width, image.Height))
                    throw new UserFacingException("The texture converter did not produce the expected PNG.");
                if (bytes.LongLength > MaxTextureOutputBytes - _textureOutputBytes)
                    throw new UserFacingException("Recovered textures reached the 256 MiB PNG output limit.");
                ct.ThrowIfCancellationRequested();
                var objectName = sourcePath[(sourcePath.IndexOf('.') + 1)..];
                var output = Path.Combine(outputDirectory, $"texture-{Guid.NewGuid():N}", objectName + ".png");
                AtomicFile.WriteAllBytes(output, bytes);
                _textureOutputBytes += bytes.LongLength;
                _textures[sourcePath] = new BlueprintExtractedTexture(sourcePath, output, texture.SRGB, image.Width,
                    image.Height, texture.IsNormalMap);
                return true;
            }
            catch (Exception error) when (BlueprintRecoveryScan.IsReadFailure(error))
            {
                Warn(sourcePath, error.Message);
                return false;
            }
        }

        private ResolvedObject? Load(string sourcePath, Func<string?, bool> supportsClass)
        {
            var split = sourcePath.IndexOf('.');
            var packagePath = sourcePath[..split];
            var file = exporter.ResolveBlueprintMeshPackage(packagePath);
            if (file is null || !exporter.IsWithinBlueprintMeshSourceBudget(file))
                return null;
            var package = exporter._provider.LoadPackage(file);
            if (package is IoPackage && !string.Equals(package.Name, packagePath, StringComparison.OrdinalIgnoreCase) ||
                !package.IsFullyLoaded || package.ExportMapLength > MaxBlueprintMeshPackageExports)
                return null;
            return BlueprintRecoveryScan.FindTopLevelAssetExport(package, sourcePath[(split + 1)..], supportsClass, ct);
        }

        private static bool IsMaterialClass(string? type) =>
            type is "/Script/Engine.Material" or "/Script/Engine.MaterialInstanceConstant";

        private static bool IsTextureClass(string? type) => type == "/Script/Engine.Texture2D";

        private static bool IsUnitValue(float value) => float.IsFinite(value) && value is >= 0 and <= 1;

        private void Warn(string sourcePath, string message)
        {
            if (warnings.Count < 1024) warnings.Add(new BlueprintMeshExtractionIssue(sourcePath, message));
            else if (warnings.Count == 1024)
                warnings.Add(new BlueprintMeshExtractionIssue(sourcePath, "Additional material and texture warnings were omitted."));
        }
    }

    internal enum MaterialSignalKind { Texture, Scalar, Color }

    internal static string? MaterialSignalChannel(string name, MaterialSignalKind kind)
    {
        var normalized = name.Replace("_", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
        return (normalized, kind) switch
        {
            ("BASECOLOR" or "ALBEDO" or "DIFFUSE", MaterialSignalKind.Texture or MaterialSignalKind.Color) => "baseColor",
            ("BASECOLORTEXTURE" or "ALBEDOTEXTURE" or "DIFFUSETEXTURE", MaterialSignalKind.Texture) => "baseColor",
            ("NORMAL" or "NORMALMAP" or "NORMALTEXTURE", MaterialSignalKind.Texture) => "normal",
            ("ROUGHNESS", MaterialSignalKind.Texture or MaterialSignalKind.Scalar) or
                ("ROUGHNESSTEXTURE" or "ROUGHNESSMAP", MaterialSignalKind.Texture) => "roughness",
            ("METALLIC" or "METALNESS", MaterialSignalKind.Texture or MaterialSignalKind.Scalar) or
                ("METALLICTEXTURE" or "METALLICMAP", MaterialSignalKind.Texture) => "metallic",
            ("AO" or "OCCLUSION" or "AMBIENTOCCLUSION", MaterialSignalKind.Texture) => "occlusion",
            ("SPECULAR", MaterialSignalKind.Scalar) => "specular",
            _ => null
        };
    }

    internal static bool HasPngDimensions(ReadOnlySpan<byte> bytes, int width, int height) =>
        bytes.Length >= 33 && bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) &&
        bytes.Slice(12, 4).SequenceEqual("IHDR"u8) &&
        BinaryPrimitives.ReadUInt32BigEndian(bytes[16..]) == width &&
        BinaryPrimitives.ReadUInt32BigEndian(bytes[20..]) == height;
}
