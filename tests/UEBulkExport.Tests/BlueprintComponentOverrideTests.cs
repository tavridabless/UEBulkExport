using System.Text.Json;

namespace UEBulkExport.Tests;

public sealed class BlueprintComponentOverrideTests
{
    private const string Owner = "/Game/Base.BP_Base_C";
    private const string Middle = "/Game/Middle.BP_Middle_C";
    private const string Child = "/Game/Child.BP_Child_C";
    private const string VariableGuid = "73CA6CC8F65E410AAAD916168032FB6B";
    private const string MeshClass = BlueprintRecovery.StaticMeshComponentClass;
    private const string OriginalMesh = "/Game/Meshes/SM_Original.SM_Original";
    private const string NewMesh = "/Game/Meshes/SM_Override.SM_Override";

    [Fact]
    public void Override_plan_pairs_the_exact_declaring_ancestor_and_serializes_only_changes()
    {
        var (baseClass, intermediate, child) = Classes(new BlueprintRecoveryComponentOverride("MeshMount", Owner, MeshClass,
            $"{Child}:MeshMount_GEN_VARIABLE", VariableGuid, RelativeLocation: new(4, 9, -3), Visible: false));
        using var temp = new TempDir();
        var destination = temp.File("plan.json");

        BlueprintRecovery.WriteBatchReconstruction(Report(child, intermediate, baseClass), destination);

        using var json = JsonDocument.Parse(File.ReadAllText(destination));
        var plans = json.RootElement.GetProperty("blueprints");
        Assert.Equal([Owner, Middle, Child], plans.EnumerateArray().Select(plan => plan.GetProperty("sourceAsset").GetString()));
        var change = Assert.Single(plans[2].GetProperty("componentOverrides").EnumerateArray());
        Assert.Equal(Owner, change.GetProperty("ownerClass").GetString());
        Assert.Equal("MeshMount", change.GetProperty("name").GetString());
        Assert.Equal(MeshClass, change.GetProperty("classPath").GetString());
        Assert.Equal(9, change.GetProperty("relativeLocation").GetProperty("y").GetDouble());
        Assert.False(change.GetProperty("visible").GetBoolean());
        Assert.False(change.TryGetProperty("relativeRotation", out _));
        Assert.False(change.TryGetProperty("staticMesh", out _));
        Assert.False(change.TryGetProperty("clearStaticMesh", out _));
    }

    [Fact]
    public void Mesh_assignment_and_explicit_clear_remain_distinct_from_inherited_mesh()
    {
        var change = new BlueprintRecoveryComponentOverride("MeshMount", Owner, MeshClass, null, VariableGuid);
        var replacement = Restore(change with { StaticMesh = NewMesh });
        var clearing = Restore(change with { ClearStaticMesh = true });

        Assert.Equal(NewMesh, Assert.Single(replacement.ComponentOverrides!).StaticMesh);
        Assert.False(Assert.Single(replacement.ComponentOverrides!).ClearStaticMesh);
        Assert.Null(Assert.Single(clearing.ComponentOverrides!).StaticMesh);
        Assert.True(Assert.Single(clearing.ComponentOverrides!).ClearStaticMesh);
        Assert.Null(Restore(change with { StaticMesh = NewMesh, ClearStaticMesh = true }).ComponentOverrides);
        Assert.Null(Restore(change).ComponentOverrides);
    }

    [Fact]
    public void Incorrect_owner_name_or_guid_and_duplicate_records_become_explicit_omissions()
    {
        var change = new BlueprintRecoveryComponentOverride("MeshMount", Owner, MeshClass, null, VariableGuid, Visible: false);
        foreach (var invalid in new[]
        {
            change with { OwnerClass = "/Game/WrongPath.BP_Base_C" }, change with { OwnerClass = Middle },
            change with { AssociatedGuid = "00000000000000000000000000000001" }, change with { Name = "Other" }
        })
        {
            var plan = Restore(invalid);
            Assert.Null(plan.ComponentOverrides);
            Assert.Contains(plan.Omissions, omission => omission.StartsWith("Inherited component ", StringComparison.Ordinal));
        }

        var duplicate = Restore(change, change with { RelativeLocation = new(1, 2, 3) });
        Assert.Null(duplicate.ComponentOverrides);
        Assert.Equal(2, duplicate.Omissions.Count(message => message.Contains("same inherited component", StringComparison.Ordinal)));
    }

    private static BlueprintReconstructionPlan Restore(params BlueprintRecoveryComponentOverride[] overrides)
    {
        var (baseClass, intermediate, child) = Classes(overrides);
        return BlueprintRecovery.CreateBatchReconstruction(Report(intermediate, child, baseClass)).Blueprints
            .Single(plan => plan.SourceAsset == Child);
    }

    private static (BlueprintRecoveryClass Base, BlueprintRecoveryClass Intermediate, BlueprintRecoveryClass Child)
        Classes(params BlueprintRecoveryComponentOverride[] overrides)
    {
        var baseClass = Class(Owner, "BP_Base_C", BlueprintRecovery.ActorParent) with
        {
            Components = [new BlueprintRecoveryComponent("DefaultSceneRoot", BlueprintRecovery.SceneComponentClass, null, true, null),
                new BlueprintRecoveryComponent("MeshMount", MeshClass, "DefaultSceneRoot", false, null,
                    RelativeRotation: new(0, 45, 0), Visible: true, StaticMesh: OriginalMesh, VariableGuid: VariableGuid)]
        };
        return (baseClass, Class(Middle, "BP_Middle_C", Owner), Class(Child, "BP_Child_C", Middle) with { ComponentOverrides = overrides });
    }

    private static BlueprintRecoveryClass Class(string path, string name, string parent) =>
        new(path, name, path, parent, [], [], null, true);

    private static BlueprintRecoveryReport Report(params BlueprintRecoveryClass[] classes) =>
        new(classes.Length, classes.Length, 0, 0, 0, false, false, false, classes, [], 0);
}
