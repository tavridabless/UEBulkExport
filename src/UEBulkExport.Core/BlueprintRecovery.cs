using System.Text.Json;
using System.Text.Json.Serialization;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Versions;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.UObject;

namespace UEBulkExport;

public sealed record BlueprintRecoveryLimits(int MaxPackages = 200, int MaxInstructions = 20_000);

public enum BlueprintBytecodeStatus { Decoded, Absent, Unparsed, Native, Truncated }
public enum BlueprintDefaultStatus { Serialized, NotSerialized, UnsupportedType, Unavailable }
public enum BlueprintParameterDirection { Input, Output, Return, Reference }

public sealed record BlueprintRecoveryDiagnostic(string PackagePath, string? ObjectPath, string Message);
public sealed record BlueprintRecoveryVariable(string Name, string Type, string Flags, int ArrayDimension,
    object? DefaultValue, BlueprintDefaultStatus DefaultStatus, string Provenance)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TypeReference { get; init; }
}
public sealed record BlueprintRecoveryParameter(string Name, string Type, string Flags, int ArrayDimension,
    BlueprintParameterDirection Direction, string Provenance)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TypeReference { get; init; }
}
public sealed record BlueprintRecoveryLocal(string Name, string Type, int ArrayDimension);
public sealed record BlueprintRecoveryInheritedDefault(string Name, string Type, object Value);
public sealed record BlueprintRecoveryInstruction(int Offset, string Opcode,
    IReadOnlyList<BlueprintRecoveryInstruction> Operands, string? Reference, object? Value, uint? TargetOffset)
{
    public string? ReferenceOwner { get; init; }
    public string? ReferenceKind { get; init; }
    public string? ReferenceName { get; init; }
}
public sealed record BlueprintRecoveryEdge(int FromOffset, uint? ToOffset, string Kind, bool TargetKnown);
public sealed record BlueprintRecoveryFunction(string Name, string Path, string Flags,
    BlueprintBytecodeStatus BytecodeStatus, IReadOnlyList<BlueprintRecoveryParameter> Parameters,
    IReadOnlyList<BlueprintRecoveryInstruction> Instructions, IReadOnlyList<BlueprintRecoveryEdge> ControlFlow,
    string? Diagnostic, bool Pure, bool ParametersComplete)
{
    public IReadOnlyList<BlueprintRecoveryLocal> LocalVariables { get; init; } = [];
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SuperFunction { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EventGraphFunction { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? EventGraphCallOffset { get; init; }
    public object? ConstantReturn => BlueprintRecovery.GetConstantReturn(this);
    public string? ParameterReturn => BlueprintRecovery.GetParameterReturn(this);
}
public sealed record BlueprintRecoveryClass(string PackagePath, string Name, string Path, string? Parent,
    IReadOnlyList<BlueprintRecoveryVariable> Variables, IReadOnlyList<BlueprintRecoveryFunction> Functions,
    string? DefaultObject, bool MetadataComplete)
{
    public IReadOnlyList<BlueprintRecoveryComponent> Components { get; init; } = [];
    public IReadOnlyList<string> ComponentIssues { get; init; } = [];
    public IReadOnlyList<BlueprintRecoveryComponentOverride> ComponentOverrides { get; init; } = [];
    public IReadOnlyList<string> ComponentOverrideIssues { get; init; } = [];
    public IReadOnlyList<BlueprintRecoveryInheritedDefault> InheritedDefaults { get; init; } = [];
}

public sealed record BlueprintRecoveryReport(
    int PackagesSelected, int PackagesInspected, int PackagesSkipped, int PackagesFailed,
    int InstructionsReported, bool PackageLimitReached, bool InstructionLimitReached, bool MetadataLimitReached,
    IReadOnlyList<BlueprintRecoveryClass> Classes, IReadOnlyList<BlueprintRecoveryDiagnostic> Diagnostics,
    int DiagnosticsOmitted)
{
    public int SchemaVersion => 1;
    public int LockedContainers { get; init; }
    public BlueprintDependencyMap DependencyMap { get; init; } = new([], []);
    public int IncompleteClasses => Classes.Count(item => !item.MetadataComplete);
    public int IncompleteFunctions => Classes.Sum(item => item.Functions.Count(function => !function.ParametersComplete ||
        function.BytecodeStatus is BlueprintBytecodeStatus.Unparsed or BlueprintBytecodeStatus.Truncated));
    public string Provenance => "CUE4Parse cooked export metadata, class default properties and Kismet expressions";
    public string OffsetKind => "Kismet VM bytecode offset";
    public string Scope => "Local cooked Blueprint-generated classes. Editor graphs and native function bodies are unavailable.";
    public string OperandScope => "Decoded expression opcodes and selected scalar operands and references. Control flow covers top-level statements.";
}

public static partial class BlueprintRecovery
{
    internal const int MaxPackageLimit = 2_000;
    internal const int MaxInstructionLimit = 100_000;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        MaxDepth = 128,
        Converters = { new JsonStringEnumConverter() }
    };

    public static BlueprintRecoveryReport Inspect(Options source, BlueprintRecoveryLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        limits ??= new BlueprintRecoveryLimits();
        ValidateLimits(limits);
        using var exporter = OpenSource(source, cancellationToken);
        return ReadReport(exporter, limits, cancellationToken);
    }

    internal static void ValidateLimits(BlueprintRecoveryLimits limits)
    {
        if (limits.MaxPackages is <= 0 or > MaxPackageLimit)
            throw new UserFacingException($"The Blueprint package limit must be between 1 and {MaxPackageLimit}.");
        if (limits.MaxInstructions is <= 0 or > MaxInstructionLimit)
            throw new UserFacingException($"The Blueprint instruction limit must be between 1 and {MaxInstructionLimit}.");
    }

    internal static BulkExporter OpenSource(Options source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Game < EGame.GAME_UE4_0)
            throw new UserFacingException("Blueprint inspection requires an Unreal Engine 4 or Unreal Engine 5 source version.");
        cancellationToken.ThrowIfCancellationRequested();

        var options = new Options
        {
            PaksDirectory = source.PaksDirectory,
            Game = source.Game,
            Platform = source.Platform,
            UsmapPath = source.UsmapPath,
            OodlePath = source.OodlePath,
            ZlibPath = source.ZlibPath,
            AesKeys = [.. source.AesKeys],
            IncludeRegex = source.IncludeRegex,
            ExcludeRegex = source.ExcludeRegex,
            PathsFile = source.PathsFile,
            SelectedPaths = source.SelectedPaths,
            Mode = ExportMode.List
        };
        Cli.Prepare(options);
        options.UsmapPath ??= Discovery.FindMappings(options.PaksDirectory, null);
        cancellationToken.ThrowIfCancellationRequested();
        Natives.Initialize(options);
        cancellationToken.ThrowIfCancellationRequested();
        return new BulkExporter(options);
    }

    internal static BlueprintRecoveryReport ReadReport(BulkExporter exporter, BlueprintRecoveryLimits limits,
        CancellationToken cancellationToken)
    {
        var report = exporter.ReadBlueprints(limits, cancellationToken);
        return report with { DependencyMap = BuildDependencyMap(report.Classes, cancellationToken) };
    }

    public static void WriteReport(BlueprintRecoveryReport report, string path) => WriteJson(report, path);

    private static void WriteJson<T>(T value, string path)
    {
        var destination = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = AtomicFile.CreateTemporaryPath(destination);
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                JsonSerializer.Serialize(stream, value, Json);
            AtomicFile.Publish(temporary, destination);
        }
        finally { AtomicFile.TryDelete(temporary); }
    }
}

public sealed partial class BulkExporter
{
    internal BlueprintRecoveryReport ReadBlueprints(BlueprintRecoveryLimits limits, CancellationToken cancellationToken)
    {
        Mount();
        _provider.ReadScriptData = true;
        var files = SelectFiles(cancellationToken, allowEmpty: true).Where(file => file.IsUePackage).ToArray();
        var scan = new BlueprintRecoveryScan(limits, cancellationToken);
        foreach (var file in files.Take(limits.MaxPackages))
        {
            cancellationToken.ThrowIfCancellationRequested();
            _provider.Files.FindPayloads(file, out var exportPayload, out _, out _);
            if (!scan.CanReadPackage(file, exportPayload)) continue;
            try
            {
                scan.ReadPackage(file.Path, _provider.LoadPackage(file));
            }
            catch (Exception e) when (BlueprintRecoveryScan.IsReadFailure(e))
            {
                scan.AddPackageFailure(file.Path, e);
            }
        }

        var locked = Containers.Where(container => container.IsLocked).ToArray();
        foreach (var container in locked)
            scan.AddDiagnostic(container.Name, null, "Container remained locked and could not be searched.");
        cancellationToken.ThrowIfCancellationRequested();
        return scan.GetReport(files.Length) with { LockedContainers = locked.Length };
    }
}

internal sealed partial class BlueprintRecoveryScan(BlueprintRecoveryLimits limits, CancellationToken cancellationToken)
{
    private const long MaxPackageSourceBytes = 32L * 1024 * 1024;
    private const int MaxExportsPerPackage = 4_096;
    private const int MaxExportSourceBytes = 4 * 1024 * 1024;
    private const int MaxMetadataItems = 8_000;
    private const int MaxDiagnostics = 256;
    private const int MaxTextLength = 1_024;
    private const int MaxObjectDepth = 32;
    private readonly List<BlueprintRecoveryClass> _classes = [];
    private readonly List<BlueprintRecoveryDiagnostic> _diagnostics = [];
    private int _inspected, _skipped, _failed, _instructions, _metadata, _diagnosticsOmitted;
    private bool _instructionLimit, _metadataLimit;

    public bool CanReadPackage(GameFile file, GameFile? exportPayload)
    {
        if (file.Size >= 0 && file.Size <= MaxPackageSourceBytes &&
            (exportPayload is null || exportPayload.Size >= 0 && exportPayload.Size <= MaxPackageSourceBytes - file.Size))
            return true;
        _skipped++;
        AddDiagnostic(file.Path, null, "Package skipped because its .uasset/.uexp source size is unknown or exceeds 32 MiB.");
        return false;
    }

    public void ReadPackage(string path, IPackage package)
    {
        if (package.ExportMapLength > MaxExportsPerPackage)
        {
            _skipped++;
            AddDiagnostic(path, null, $"Package skipped because it has more than {MaxExportsPerPackage} exports.");
            return;
        }
        if (!package.IsFullyLoaded)
        {
            _failed++;
            AddDiagnostic(path, null, "CUE4Parse could not load the package export table.");
            return;
        }

        for (var index = 0; index < package.ExportMapLength; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var export = package.ResolvePackageIndex(new FPackageIndex(package, index + 1));
            var type = export?.Class?.Name.Text;
            if (export is null) continue;
            if (type is null && export.Name.Text.EndsWith("_C", StringComparison.Ordinal))
                AddDiagnostic(path, ObjectPath(export), "Export class reference could not be resolved.");
            if (type?.EndsWith("BlueprintGeneratedClass", StringComparison.Ordinal) != true) continue;
            if (!TakeMetadata()) break;

            try { _ = package.CanDeserialize; }
            catch (Exception e) when (IsReadFailure(e))
            {
                AddPackageFailure(path, e);
                return;
            }
            InspectClass(path, export);
        }
        _inspected++;
    }

    private void InspectClass(string packagePath, ResolvedObject export)
    {
        var name = Clip(export.Name.Text);
        var path = ObjectPath(export) ?? Clip($"{packagePath}:{name}");
        var parent = ObjectPath(export.Super);
        if (!IsWithinExportSize(export))
        {
            _metadataLimit = true;
            AddDiagnostic(packagePath, path, "Generated class skipped because its export exceeds the 4 MiB source-byte limit.");
            _classes.Add(new BlueprintRecoveryClass(packagePath, name, path, parent, [], [], null, false));
            return;
        }
        UBlueprintGeneratedClass? generated;
        try { generated = export.Load<UBlueprintGeneratedClass>(); }
        catch (Exception e) when (IsReadFailure(e))
        {
            AddDiagnostic(packagePath, path, Clip(e.Message));
            generated = null;
        }
        if (generated?.FuncMap is null || generated.SuperStruct is null)
        {
            AddDiagnostic(packagePath, path, "CUE4Parse could not deserialize the generated class metadata.");
            _classes.Add(new BlueprintRecoveryClass(packagePath, name, path, parent, [], [], null, false));
            return;
        }

        parent = ObjectPath(generated.SuperStruct.ResolvedObject) ?? parent;
        var complete = parent is not null;
        if (!complete) AddDiagnostic(packagePath, path, "The generated class's parent reference could not be resolved.");
        var properties = ReadProperties(generated, packagePath, path, ref complete);
        var hasBlueprintParent = parent is not null && !parent.StartsWith("/Script/", StringComparison.OrdinalIgnoreCase);
        var defaults = LoadDefaults(generated, properties.Count > 0 || hasBlueprintParent, packagePath, path);
        var variables = new List<BlueprintRecoveryVariable>();
        foreach (var property in properties)
        {
            var (value, status) = ReadDefault(property, defaults);
            variables.Add(new BlueprintRecoveryVariable(property.Name, property.Type, property.Flags.ToString(),
                property.Dimension, value, status, property.Provenance) { TypeReference = property.TypeReference });
        }

        var functions = new List<BlueprintRecoveryFunction>();
        foreach (var entry in generated.FuncMap)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TakeMetadata()) { complete = false; break; }
            functions.Add(InspectFunction(packagePath, path, Clip(entry.Key.Text), entry.Value));
        }
        var components = ReadComponents(generated, export, out var componentIssues);
        var componentOverrides = ReadComponentOverrides(generated, export, out var componentOverrideIssues);
        _classes.Add(new BlueprintRecoveryClass(packagePath, name, path, parent, variables, functions,
            ObjectPath(generated.ClassDefaultObject?.ResolvedObject), complete)
        {
            Components = components,
            ComponentIssues = componentIssues,
            ComponentOverrides = componentOverrides,
            ComponentOverrideIssues = componentOverrideIssues,
            InheritedDefaults = hasBlueprintParent ? ReadInheritedDefaults(defaults, properties) : []
        });
    }

    private IReadOnlyCollection<PropertyMetadata> ReadProperties(UStruct structure, string packagePath,
        string objectPath, ref bool complete)
    {
        var properties = new Dictionary<string, PropertyMetadata>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in structure.ChildProperties ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (field is not FProperty property) continue;
            if (!TakeMetadata()) { complete = false; return properties.Values; }
            var name = Clip(property.Name.Text);
            properties.TryAdd(name, new PropertyMetadata(name, property.GetType().Name[1..], property.PropertyFlags,
                property.ArrayDim, "ChildProperties", ReadPropertyType(property)));
        }

        // UE4 releases before FProperty migrated property exports through the UField chain.
        if (structure.ChildProperties is not null) return properties.Values;
        var visited = new HashSet<int>();
        foreach (var child in structure.Children ?? [])
        {
            for (var current = child; current is { IsExport: true } && visited.Add(current.Index);)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (visited.Count > MaxExportsPerPackage || !TakeMetadata())
                {
                    complete = false;
                    _metadataLimit = true;
                    return properties.Values;
                }
                try
                {
                    var field = current.ResolvedObject is { } export && IsWithinExportSize(export) ? export.Load<UField>() : null;
                    if (field is UProperty property)
                    {
                        var name = Clip(property.Name);
                        properties.TryAdd(name, new PropertyMetadata(name, property.GetType().Name[1..],
                            property.PropertyFlags, property.ArrayDim, "LegacyChildren", ReadPropertyType(property)));
                    }
                    if (field is null) { complete = false; break; }
                    current = field.Next;
                }
                catch (Exception e) when (IsReadFailure(e))
                {
                    AddDiagnostic(packagePath, objectPath, Clip(e.Message));
                    complete = false;
                    break;
                }
            }
        }
        return properties.Values;
    }

    private static string? ReadPropertyType(object property)
    {
        var reference = property switch
        {
            FClassProperty field => field.MetaClass,
            FObjectProperty field => field.PropertyClass,
            FInterfaceProperty field => field.InterfaceClass,
            FStructProperty field => field.Struct,
            FEnumProperty field => field.Enum,
            UClassProperty field => field.MetaClass,
            UObjectPropertyBase field => field.PropertyClass,
            UInterfaceProperty field => field.InterfaceClass,
            UStructProperty field => field.Struct,
            UEnumProperty field => field.Enum,
            _ => null
        };
        try { return ObjectPath(reference?.ResolvedObject); }
        catch (Exception error) when (IsReadFailure(error)) { return null; }
    }

    private Dictionary<string, FPropertyTag>? LoadDefaults(UBlueprintGeneratedClass generated, bool hasProperties,
        string packagePath, string classPath)
    {
        if (!hasProperties || generated.ClassDefaultObject is not { IsExport: true } reference) return null;
        try
        {
            var source = reference.ResolvedObject is { } export && IsWithinExportSize(export) ? export.Load() : null;
            if (source is null || source.Properties.Count > MaxExportsPerPackage)
            {
                AddDiagnostic(packagePath, classPath, "Class default properties could not be read within the metadata limit.");
                return null;
            }
            var result = new Dictionary<string, FPropertyTag>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in source.Properties)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (property.ArrayIndex == 0) result.TryAdd(property.Name.Text, property);
            }
            return result;
        }
        catch (Exception e) when (IsReadFailure(e))
        {
            AddDiagnostic(packagePath, classPath, Clip(e.Message));
            return null;
        }
    }

    private static (object?, BlueprintDefaultStatus) ReadDefault(PropertyMetadata property,
        Dictionary<string, FPropertyTag>? defaults)
    {
        if (property.Dimension != 1) return (null, BlueprintDefaultStatus.UnsupportedType);
        if (defaults is null) return (null, BlueprintDefaultStatus.Unavailable);
        if (!defaults.TryGetValue(property.Name, out var tag)) return (null, BlueprintDefaultStatus.NotSerialized);
        var value = tag.Tag?.GenericValue;
        return IsScalar(value) ? (value, BlueprintDefaultStatus.Serialized) : (null, BlueprintDefaultStatus.UnsupportedType);
    }

    private BlueprintRecoveryFunction InspectFunction(string packagePath, string classPath, string name,
        FPackageIndex reference)
    {
        // Follow local FuncMap entries only. Imports can carry a name but can't establish a body.
        var path = Clip($"{classPath}:{name}");
        try
        {
            var export = reference.ResolvedObject;
            path = ObjectPath(export) ?? path;
            if (reference.IsExport && export is not null && !IsWithinExportSize(export))
            {
                _metadataLimit = true;
                return new BlueprintRecoveryFunction(name, path, "", BlueprintBytecodeStatus.Truncated, [], [], [],
                    "The export exceeds the 4 MiB source-byte limit.", false, false);
            }
            var function = reference.IsExport ? export?.Load<UFunction>() : null;
            if (function?.Children is null || function.FunctionFlags == 0)
                return new BlueprintRecoveryFunction(name, path, "", BlueprintBytecodeStatus.Unparsed, [], [], [],
                    "The function export could not be deserialized locally.", false, false);

            var complete = true;
            var properties = ReadProperties(function, packagePath, path, ref complete);
            var parameters = properties
                .Where(property => property.Flags.HasFlag(EPropertyFlags.Parm))
                .Select(property => new BlueprintRecoveryParameter(property.Name, property.Type, property.Flags.ToString(),
                    property.Dimension, Direction(property.Flags), property.Provenance) { TypeReference = property.TypeReference })
                .ToArray();
            var (instructions, status, diagnostic) = ReadInstructions(function);
            return new BlueprintRecoveryFunction(name, path, function.FunctionFlags.ToString(), status, parameters,
                instructions, BuildFlow(instructions), diagnostic,
                function.FunctionFlags.HasFlag(EFunctionFlags.FUNC_BlueprintPure), complete)
            {
                LocalVariables = properties.Where(property => !property.Flags.HasFlag(EPropertyFlags.Parm))
                    .Select(property => new BlueprintRecoveryLocal(property.Name, property.Type, property.Dimension)).ToArray(),
                SuperFunction = ObjectPath(function.SuperStruct?.ResolvedObject ?? export?.Super),
                EventGraphFunction = ObjectPath(function.EventGraphFunction?.ResolvedObject),
                EventGraphCallOffset = function.EventGraphFunction is { IsNull: false } ? function.EventGraphCallOffset : null
            };
        }
        catch (Exception e) when (IsReadFailure(e))
        {
            AddDiagnostic(packagePath, path, Clip(e.Message));
            return new BlueprintRecoveryFunction(name, path, "", BlueprintBytecodeStatus.Unparsed, [], [], [],
                "CUE4Parse could not process the function export.", false, false);
        }
    }

    private static BlueprintParameterDirection Direction(EPropertyFlags flags)
    {
        if (flags.HasFlag(EPropertyFlags.ReturnParm)) return BlueprintParameterDirection.Return;
        if (flags.HasFlag(EPropertyFlags.ReferenceParm)) return BlueprintParameterDirection.Reference;
        return flags.HasFlag(EPropertyFlags.OutParm) ? BlueprintParameterDirection.Output : BlueprintParameterDirection.Input;
    }

    private bool TakeMetadata()
    {
        if (_metadata < MaxMetadataItems) { _metadata++; return true; }
        _metadataLimit = true;
        return false;
    }

    public void AddPackageFailure(string path, Exception error)
    {
        _failed++;
        AddDiagnostic(path, null, Clip(error.Message));
    }

    public void AddDiagnostic(string path, string? objectPath, string message)
    {
        if (_diagnostics.Count < MaxDiagnostics)
            _diagnostics.Add(new BlueprintRecoveryDiagnostic(Clip(path), objectPath, message));
        else _diagnosticsOmitted++;
    }

    public BlueprintRecoveryReport GetReport(int selected) => new(selected, _inspected, _skipped, _failed,
        _instructions, selected > limits.MaxPackages, _instructionLimit, _metadataLimit, _classes, _diagnostics, _diagnosticsOmitted);

    private static bool IsWithinExportSize(ResolvedObject export) => export.Package switch
    {
        Package package when export.ExportIndex >= 0 && export.ExportIndex < package.ExportMap.Length =>
            package.ExportMap[export.ExportIndex].SerialSize is >= 0 and <= MaxExportSourceBytes,
        IoPackage package when export.ExportIndex >= 0 && export.ExportIndex < package.ExportMap.Length =>
            package.ExportMap[export.ExportIndex].CookedSerialSize <= MaxExportSourceBytes,
        _ => false
    };

    internal static bool IsReadFailure(Exception error) => error is not OperationCanceledException and not OutOfMemoryException;

    private static string? ObjectPath(ResolvedObject? value)
    {
        if (value is null) return null;
        // Use the same UE separators as CUE4Parse, with a cap on following nested outers.
        var names = new List<string>();
        for (; value is not null && names.Count < MaxObjectDepth; value = value.Outer)
            names.Add(Clip(value.Name.Text));
        if (value is not null) return null;
        names.Reverse();
        if (names.Count <= 2) return Clip(string.Join('.', names));
        return Clip($"{names[0]}.{names[1]}:{string.Join('.', names.Skip(2))}");
    }

    private static bool IsScalar(object? value) => value switch
    {
        string text => text.Length <= MaxTextLength,
        float number => float.IsFinite(number),
        double number => double.IsFinite(number),
        bool or byte or sbyte or short or ushort or int or uint or long or ulong => true,
        _ => false
    };

    private static string Clip(string value) => value.Length <= MaxTextLength ? value : value[..(MaxTextLength - 1)] + "…";

    private readonly record struct PropertyMetadata(string Name, string Type, EPropertyFlags Flags,
        int Dimension, string Provenance, string? TypeReference);
}
