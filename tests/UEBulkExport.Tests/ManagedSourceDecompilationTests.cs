using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Xml.Linq;

namespace UEBulkExport.Tests;

public sealed class ManagedSourceDecompilationTests
{
    [Fact]
    public void Project_uses_only_matching_local_reference_and_removes_compiler_module_attribute()
    {
        using var temp = new TempDir();
        var input = Path.Combine(temp.Dir("input"), "Fixture.dll");
        var wrong = Path.Combine(temp.Dir("wrong"), "LocalDependency.dll");
        var right = Path.Combine(temp.Dir("right"), "LocalDependency.dll");
        var output = temp.Dir("output");
        var requested = new Version(2, 0, 0, 0);
        WriteAssembly(input, "Fixture", new Version(1, 0, 0, 0), requested);
        WriteAssembly(wrong, "LocalDependency", new Version(1, 0, 0, 0));
        WriteAssembly(right, "LocalDependency", requested);

        var incomplete = ManagedSourceDecompilation.Run(input, output, [Path.GetDirectoryName(wrong)!]);

        Assert.True(incomplete.HasGaps);
        Assert.Contains(incomplete.UnresolvedReferences, reference => reference.StartsWith("LocalDependency, Version=2.0.0.0"));
        Assert.False(Directory.Exists(Path.Combine(incomplete.OutputDirectory, "References")));

        var report = ManagedSourceDecompilation.Run(input, output, [Path.GetDirectoryName(wrong)!, Path.GetDirectoryName(right)!]);

        Assert.DoesNotContain(report.UnresolvedReferences, reference => reference.StartsWith("LocalDependency,"));
        Assert.Equal("net10.0", report.TargetFramework);
        Assert.NotEqual(incomplete.OutputDirectory, report.OutputDirectory);
        var project = XElement.Load(report.ProjectPath);
        Assert.Equal("net10.0", project.Descendants("TargetFramework").Single().Value);
        Assert.Contains(project.Descendants("HintPath"), hint => hint.Value == "References/LocalDependency.dll");
        Assert.Equal(File.ReadAllBytes(right), File.ReadAllBytes(Path.Combine(report.OutputDirectory, "References", "LocalDependency.dll")));
        var sourceText = File.ReadAllText(Path.Combine(report.OutputDirectory, "Decompiled.cs"));
        Assert.Contains("RecoveredType", sourceText);
        Assert.DoesNotContain("[module: RefSafetyRules", sourceText);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(input)!, "*.pdb"));
        Assert.Empty(Directory.GetDirectories(output, "*.partial"));
    }

    [Fact]
    public void Malformed_or_cancelled_input_does_not_publish_a_partial_project()
    {
        using var temp = new TempDir();
        var malformed = temp.File("garbage.dll");
        var output = temp.Dir("output");
        Assert.Throws<UserFacingException>(() => ManagedSourceDecompilation.Run(malformed, output));
        Assert.Throws<OperationCanceledException>(() =>
            ManagedSourceDecompilation.Run(malformed, output, ct: new CancellationToken(true)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(output));
    }

    private static void WriteAssembly(string path, string name, Version version, Version? localDependency = null)
    {
        var metadata = new MetadataBuilder();
        metadata.AddModule(0, metadata.GetOrAddString(name + ".dll"), metadata.GetOrAddGuid(Guid.NewGuid()), default, default);
        var assembly = metadata.AddAssembly(metadata.GetOrAddString(name), version, default, default, 0, AssemblyHashAlgorithm.None);
        var runtime = metadata.AddAssemblyReference(metadata.GetOrAddString("System.Runtime"), new Version(10, 0, 0, 0),
            default, metadata.GetOrAddBlob(Convert.FromHexString("B03F5F7F11D50A3A")), 0, default);
        if (localDependency is not null)
            metadata.AddAssemblyReference(metadata.GetOrAddString("LocalDependency"), localDependency, default, default, 0, default);

        var framework = Constructor(metadata, runtime, "System.Runtime.Versioning", "TargetFrameworkAttribute", stringParameter: true);
        var frameworkValue = new BlobBuilder();
        frameworkValue.WriteUInt16(1);
        frameworkValue.WriteSerializedString(".NETCoreApp,Version=v10.0");
        frameworkValue.WriteUInt16(0);
        metadata.AddCustomAttribute(assembly, framework, metadata.GetOrAddBlob(frameworkValue));
        var rules = Constructor(metadata, runtime, "System.Runtime.CompilerServices", "RefSafetyRulesAttribute", stringParameter: false);
        var rulesValue = new BlobBuilder();
        rulesValue.WriteUInt16(1);
        rulesValue.WriteInt32(11);
        rulesValue.WriteUInt16(0);
        metadata.AddCustomAttribute(MetadataTokens.EntityHandle(1), rules, metadata.GetOrAddBlob(rulesValue));
        var objectType = metadata.AddTypeReference(runtime, metadata.GetOrAddString("System"), metadata.GetOrAddString("Object"));
        var fields = MetadataTokens.FieldDefinitionHandle(1);
        var methods = MetadataTokens.MethodDefinitionHandle(1);
        metadata.AddTypeDefinition(TypeAttributes.NotPublic, default, metadata.GetOrAddString("<Module>"), default, fields, methods);
        metadata.AddTypeDefinition(TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed, default,
            metadata.GetOrAddString("RecoveredType"), objectType, fields, methods);

        var output = new BlobBuilder();
        var writer = new ManagedPEBuilder(new PEHeaderBuilder(imageCharacteristics: Characteristics.ExecutableImage | Characteristics.Dll),
            new MetadataRootBuilder(metadata), new BlobBuilder(), flags: CorFlags.ILOnly);
        writer.Serialize(output);
        File.WriteAllBytes(path, output.ToArray());
    }

    private static MemberReferenceHandle Constructor(MetadataBuilder metadata, AssemblyReferenceHandle runtime, string space,
        string name, bool stringParameter)
    {
        var type = metadata.AddTypeReference(runtime, metadata.GetOrAddString(space), metadata.GetOrAddString(name));
        var signature = new BlobBuilder();
        new BlobEncoder(signature).MethodSignature(isInstanceMethod: true).Parameters(1, result => result.Void(), parameters =>
        {
            var parameter = parameters.AddParameter().Type();
            if (stringParameter) parameter.String();
            else parameter.Int32();
        });
        return metadata.AddMemberReference(type, metadata.GetOrAddString(".ctor"), metadata.GetOrAddBlob(signature));
    }
}
