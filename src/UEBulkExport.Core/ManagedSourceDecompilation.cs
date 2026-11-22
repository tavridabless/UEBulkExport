using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.OutputVisitor;
using ICSharpCode.Decompiler.CSharp.Transforms;
using ICSharpCode.Decompiler.Metadata;

namespace UEBulkExport;

public sealed record ManagedSourceDecompilationReport(string OutputDirectory, string ProjectPath, string TargetFramework,
    IReadOnlyList<string> UnresolvedReferences, IReadOnlyList<string> Diagnostics)
{
    public int SchemaVersion => 1;
    public string Provenance => "C# reconstructed from managed IL using ILSpy.";
    public bool HasGaps => UnresolvedReferences.Count > 0 || Diagnostics.Count > 0;
}

/// <summary>Produces an editable SDK project by reading metadata and IL without loading the assembly.</summary>
public static class ManagedSourceDecompilation
{
    internal const long MaxAssemblyBytes = 128L * 1024 * 1024;
    private const string ProjectName = "Decompiled.csproj";

    public static ManagedSourceDecompilationReport Run(string assembly, string outputParent,
        IReadOnlyList<string>? referenceDirectories = null, CancellationToken ct = default)
    {
        try
        {
            return Decompile(Path.GetFullPath(assembly), Path.GetFullPath(outputParent), referenceDirectories ?? [], ct);
        }
        catch (DecompilerException error)
        {
            throw new UserFacingException("ILSpy could not reconstruct this assembly.", error.Message);
        }
        catch (Exception error) when (error is BadImageFormatException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new UserFacingException("The managed assembly or output could not be read or written.", error.Message);
        }
    }

    private static ManagedSourceDecompilationReport Decompile(string input, string parent, IReadOnlyList<string> searchPaths,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var module = OpenModule(input, PEStreamOptions.Default);
        var metadata = module.Metadata;
        var header = module.Reader.PEHeaders.CorHeader;
        if (!metadata.IsAssembly || header is null || !header.Flags.HasFlag(CorFlags.ILOnly) ||
            HasAttribute(metadata, "System.Runtime.CompilerServices", "ReferenceAssemblyAttribute") ||
            metadata.AssemblyFiles.Any(handle => metadata.GetAssemblyFile(handle).ContainsMetadata))
            throw new UserFacingException("Choose a single-module managed .NET implementation assembly with IL.",
                "Reference assemblies, native code and linked .netmodule inputs are outside this workflow.");

        var definition = metadata.GetAssemblyDefinition();
        var name = metadata.GetString(definition.Name);
        if (!IsSimpleName(name))
            throw new UserFacingException("The assembly name cannot be used as a portable project name.");
        var (declaredFramework, targetFramework) = ReadTargetFramework(metadata);
        var executable = header.EntryPointTokenOrRelativeVirtualAddress != 0;
        var diagnostics = new List<string>();
        if (metadata.ManifestResources.Count > 0)
            diagnostics.Add("Embedded resources were omitted. The project may need the original resources.");
        if (!definition.PublicKey.IsNil)
            diagnostics.Add("The generated project is unsigned. Supply a signing key if your dependencies require it.");
        if (metadata.MethodDefinitions.Any(method => metadata.GetMethodDefinition(method).Attributes.HasFlag(MethodAttributes.PinvokeImpl)))
            diagnostics.Add("C# interop declarations were reconstructed. Supply the referenced native libraries to run them.");

        var folders = new[] { Path.GetDirectoryName(input)! }.Concat(searchPaths.Select(Path.GetFullPath))
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray();
        if (folders.Any(folder => !Directory.Exists(folder)))
            throw new UserFacingException("A local assembly reference folder is missing.");
        using var resolver = new ManagedDecompilationResolver(declaredFramework, folders, ct);
        var references = resolver.CollectReferences(module, diagnostics);
        var settings = new DecompilerSettings(LanguageVersion.CSharp14_0);
        var decompiler = new CSharpDecompiler(module, resolver, settings) { CancellationToken = ct };
        decompiler.AstTransforms.Add(new EscapeInvalidIdentifiers());
        decompiler.AstTransforms.Add(new RemoveCLSCompliantAttribute());
        decompiler.AstTransforms.Add(new RemoveCompilerGeneratedAssemblyAttributes());
        var syntax = decompiler.DecompileWholeModuleAsSingleFile(sortTypes: true);
        diagnostics.AddRange(decompiler.Errors.Take(100).Select(error => "ILSpy: " + error.Message));
        if (decompiler.Errors.Count > 100) diagnostics.Add("Additional ILSpy diagnostics were omitted.");

        Directory.CreateDirectory(parent);
        var output = Path.Combine(parent, "decompiled-" + Guid.NewGuid().ToString("N"));
        var staging = output + ".partial";
        try
        {
            Directory.CreateDirectory(staging);
            using (var writer = new StreamWriter(Path.Combine(staging, "Decompiled.cs"), false, new UTF8Encoding(false)))
                syntax.AcceptVisitor(new CSharpOutputVisitor(writer, settings.CSharpFormattingOptions));
            ct.ThrowIfCancellationRequested();
            WriteProject(staging, name, targetFramework, executable, references, ct);
            var report = new ManagedSourceDecompilationReport(output, Path.Combine(output, ProjectName), targetFramework,
                resolver.UnresolvedReferences, diagnostics.AsReadOnly());
            AtomicFile.WriteAllBytes(Path.Combine(staging, "managed-decompile-report.json"),
                JsonSerializer.SerializeToUtf8Bytes(report with { OutputDirectory = ".", ProjectPath = ProjectName },
                    ManagedSourceRecovery.Json));
            ct.ThrowIfCancellationRequested();
            Directory.Move(staging, output);
            return report;
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                try { Directory.Delete(staging, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static void WriteProject(string staging, string name, string targetFramework, bool executable,
        ManagedDecompilationReferences references, CancellationToken ct)
    {
        var project = new XElement("Project", new XAttribute("Sdk", "Microsoft.NET.Sdk"),
            new XElement("PropertyGroup",
                new XElement("TargetFramework", targetFramework),
                new XElement("AssemblyName", name),
                new XElement("OutputType", executable ? "Exe" : "Library"),
                new XElement("LangVersion", "14.0"),
                new XElement("AllowUnsafeBlocks", true),
                new XElement("GenerateAssemblyInfo", false)));
        var items = new XElement("ItemGroup");
        foreach (var framework in references.Frameworks)
            items.Add(new XElement("FrameworkReference", new XAttribute("Include", framework)));
        if (references.Libraries.Count > 0)
        {
            Directory.CreateDirectory(Path.Combine(staging, "References"));
            foreach (var (referenceName, sourcePath) in references.Libraries)
            {
                ct.ThrowIfCancellationRequested();
                var fileName = referenceName + ".dll";
                File.Copy(sourcePath, Path.Combine(staging, "References", fileName));
                items.Add(new XElement("Reference", new XAttribute("Include", referenceName),
                    new XElement("HintPath", "References/" + fileName), new XElement("Private", true)));
            }
        }
        if (items.HasElements) project.Add(items);
        project.Save(Path.Combine(staging, ProjectName));
    }

    private static (string Declared, string Sdk) ReadTargetFramework(MetadataReader metadata)
    {
        var declared = ReadAttribute(metadata, "System.Runtime.Versioning", "TargetFrameworkAttribute") ?? "";
        const string net = ".NETCoreApp,Version=v";
        const string standard = ".NETStandard,Version=v";
        var sdk = declared switch
        {
            _ when declared.StartsWith(net, StringComparison.Ordinal) &&
                Version.TryParse(declared[net.Length..], out var version) && version.Major is >= 5 and <= 99 &&
                version.Build < 0 => $"net{version.Major}.{version.Minor}",
            _ when declared is standard + "2.0" or standard + "2.1" => "netstandard" + declared[standard.Length..],
            _ => throw new UserFacingException("The assembly does not declare a supported SDK target framework.",
                "Use an assembly that declares .NET 5 or newer, or .NET Standard 2.0 or 2.1.")
        };
        if (ReadAttribute(metadata, "System.Runtime.Versioning", "TargetPlatformAttribute") is { } platform)
        {
            if (declared.StartsWith(net, StringComparison.Ordinal) &&
                platform.StartsWith("Windows", StringComparison.OrdinalIgnoreCase) &&
                Version.TryParse(platform[7..], out var windows))
                sdk += "-windows" + windows;
            else
                throw new UserFacingException("The assembly declares a target platform outside the supported SDK project formats.",
                    "The current project writer supports cross-platform managed projects and Windows targets.");
        }
        return (declared, sdk);
    }

    private static bool HasAttribute(MetadataReader metadata, string space, string name) =>
        !FindAttribute(metadata, space, name).IsNil;

    private static string? ReadAttribute(MetadataReader metadata, string space, string name)
    {
        var handle = FindAttribute(metadata, space, name);
        if (handle.IsNil) return null;
        var value = metadata.GetBlobReader(metadata.GetCustomAttribute(handle).Value);
        return value.ReadUInt16() == 1 ? value.ReadSerializedString() : null;
    }

    private static CustomAttributeHandle FindAttribute(MetadataReader metadata, string space, string name)
    {
        foreach (var handle in metadata.GetAssemblyDefinition().GetCustomAttributes())
        {
            var attribute = metadata.GetCustomAttribute(handle);
            if (attribute.Constructor.Kind != HandleKind.MemberReference) continue;
            var member = metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
            if (member.Parent.Kind != HandleKind.TypeReference) continue;
            var type = metadata.GetTypeReference((TypeReferenceHandle)member.Parent);
            if (metadata.StringComparer.Equals(type.Namespace, space) && metadata.StringComparer.Equals(type.Name, name))
                return handle;
        }
        return default;
    }

    internal static PEFile OpenModule(string path, PEStreamOptions options)
    {
        var stream = File.OpenRead(path);
        PEReader? reader = null;
        try
        {
            if (stream.Length > MaxAssemblyBytes) throw new BadImageFormatException("A managed assembly exceeds 128 MiB.");
            reader = new PEReader(stream, options);
            return new PEFile(path, reader);
        }
        catch
        {
            reader?.Dispose();
            stream.Dispose();
            throw;
        }
    }

    private static readonly HashSet<string> WindowsDevices = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    internal static bool IsSimpleName(string name) => name.Length is > 0 and <= 120 &&
        char.IsAsciiLetterOrDigit(name[0]) && char.IsAsciiLetterOrDigit(name[^1]) &&
        name.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_') &&
        !WindowsDevices.Contains(name.Split('.', 2)[0]);
}
