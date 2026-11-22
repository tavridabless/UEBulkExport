namespace UEBulkExport.Tests;

public sealed class BlueprintDependencyTests
{
    private const string RootPath = "/Game/Root.BP_Root_C";
    private const string TargetPath = "/Game/Library.BFL_Operations_C";
    private const string OtherLibraryPath = "/Game/Other.BFL_Operations_C";

    [Fact]
    public void Map_retains_nested_calls_exact_ids_and_distinct_resolution_states()
    {
        var function = new BlueprintRecoveryFunction("Compute", RootPath + ":Compute", "", BlueprintBytecodeStatus.Decoded,
            [new BlueprintRecoveryParameter("Item", "ObjectProperty", "", 1, BlueprintParameterDirection.Input, "ChildProperties")
            {
                TypeReference = "/Game/Item.BP_Item_C"
            }],
            [Instruction(0, "EX_Return", [
                Instruction(1, "EX_LocalFinalFunction") with { ReferenceOwner = TargetPath, ReferenceName = "Resolve" },
                Instruction(2, "EX_FinalFunction") with { ReferenceOwner = OtherLibraryPath, ReferenceName = "Resolve" },
                Instruction(3, "EX_Context", [
                    Instruction(4, "EX_ObjectConst") with
                    {
                        ReferenceOwner = TargetPath,
                        ReferenceKind = BlueprintRecoveryScan.BlueprintDefaultObjectImport
                    },
                    Instruction(5, "EX_LocalVirtualFunction", [
                        Instruction(6, "EX_VirtualFunction") with { Reference = "DynamicArgument" }
                    ]) with { Reference = "Resolve" }
                ])
            ])], [], null, false, true);
        var root = Class(RootPath, "BP_Root_C", BlueprintRecovery.ActorParent, function);
        var library = Class(TargetPath, "BFL_Operations_C", BlueprintRecovery.FunctionLibraryParent);

        var map = BlueprintRecovery.BuildDependencyMap([root, library], default);

        Assert.Contains(map.Nodes, node => node.Path == TargetPath && node.IsInspected);
        Assert.Contains(map.Nodes, node => node.Path == OtherLibraryPath && !node.IsInspected);
        Assert.Contains(map.Edges, edge => edge.Source == RootPath && edge.Kind == BlueprintDependencyKind.Parent &&
            edge.Target == BlueprintRecovery.ActorParent && edge.Resolution == BlueprintDependencyResolution.Native);
        Assert.Contains(map.Edges, edge => edge.Kind == BlueprintDependencyKind.Type &&
            edge.Target == "/Game/Item.BP_Item_C" && edge.Resolution == BlueprintDependencyResolution.OutsideReport &&
            edge.SourceFunction == "Compute" && edge.Member == "Item");
        Assert.Collection(map.Edges.Where(edge => edge.Kind == BlueprintDependencyKind.Call),
            edge =>
            {
                Assert.Equal(TargetPath, edge.Target);
                Assert.Equal(BlueprintDependencyResolution.Inspected, edge.Resolution);
                Assert.Equal("Compute", edge.SourceFunction);
                Assert.Equal(1, edge.Offset);
                Assert.False(edge.IsRequiredForReconstruction);
            },
            edge =>
            {
                Assert.Equal(OtherLibraryPath, edge.Target);
                Assert.Equal(BlueprintDependencyResolution.OutsideReport, edge.Resolution);
            },
            edge =>
            {
                Assert.Equal(TargetPath, edge.Target);
                Assert.Equal(BlueprintDependencyResolution.Inspected, edge.Resolution);
                Assert.Equal(5, edge.Offset);
            },
            edge =>
            {
                Assert.Equal(BlueprintDependencyResolution.Unknown, edge.Resolution);
                Assert.Equal("DynamicArgument", edge.Member);
            });
    }

    [Fact]
    public void Linked_plan_needs_a_unique_exact_root_and_respects_cancellation()
    {
        var root = Class(RootPath, "BP_Root_C", BlueprintRecovery.ActorParent);
        var library = Class(TargetPath, "BFL_Operations_C", BlueprintRecovery.FunctionLibraryParent);

        var plan = BlueprintRecovery.CreateLinkedReconstruction([root, library], RootPath);

        Assert.Equal(2, plan.SchemaVersion);
        Assert.Equal(RootPath, Assert.Single(plan.Blueprints).SourceAsset);
        Assert.Throws<UserFacingException>(() => BlueprintRecovery.CreateLinkedReconstruction([root, library], "BP_Root_C"));
        Assert.Throws<UserFacingException>(() => BlueprintRecovery.CreateLinkedReconstruction([root, root], RootPath));
        Assert.Throws<OperationCanceledException>(() =>
            BlueprintRecovery.CreateLinkedReconstruction([root], RootPath, new CancellationToken(true)));
    }

    [Fact]
    public void Map_records_mesh_dependencies_as_asset_paths_without_adding_blueprint_classes()
    {
        const string mesh = "/Game/Geometry/SM_Probe.SM_Probe";
        var root = Class(RootPath, "BP_Root_C", BlueprintRecovery.ActorParent) with
        {
            Components = [new("Mesh", BlueprintRecovery.StaticMeshComponentClass, "DefaultSceneRoot", false, null, StaticMesh: mesh)]
        };
        const string replacement = "/Engine/BasicShapes/Cylinder.Cylinder";
        var child = Class("/Game/Child.BP_Child_C", "BP_Child_C", RootPath) with
        {
            ComponentOverrides = [new("Mesh", RootPath, BlueprintRecovery.StaticMeshComponentClass, null, null,
                StaticMesh: replacement)]
        };

        var map = BlueprintRecovery.BuildDependencyMap([root, child], default);
        var edge = Assert.Single(map.Edges, item => item.Kind == BlueprintDependencyKind.Asset && item.Source == RootPath);

        Assert.Equal(RootPath, edge.Source);
        Assert.Equal(mesh, edge.Target);
        Assert.Equal("Mesh", edge.Member);
        Assert.Equal(BlueprintDependencyResolution.OutsideReport, edge.Resolution);
        Assert.Contains(map.Nodes, item => item.Path == mesh && !item.IsInspected);
        Assert.Contains(map.Edges, item => item.Kind == BlueprintDependencyKind.Asset &&
            item.Source == child.Path && item.Target == replacement && item.Member == "Mesh");
        Assert.Equal(2, BlueprintRecovery.CreateLinkedReconstruction([root, child], child.Path).Blueprints.Count);
    }

    private static BlueprintRecoveryClass Class(string path, string name, string parent,
        params BlueprintRecoveryFunction[] functions) => new(path, name, path, parent, [], functions, null, true);

    private static BlueprintRecoveryInstruction Instruction(int offset, string opcode,
        BlueprintRecoveryInstruction[]? operands = null) => new(offset, opcode, operands ?? [], null, null, null);
}
