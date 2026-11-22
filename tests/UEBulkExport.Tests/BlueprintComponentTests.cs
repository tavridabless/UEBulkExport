namespace UEBulkExport.Tests;

public sealed class BlueprintComponentTests
{
    [Fact]
    public void Project_keeps_parent_first_exact_scene_components_and_avoids_duplicate_variables()
    {
        var root = Scene("DefaultSceneRoot", null, isDefault: true);
        var middle = Scene("Pivot", root.Name) with
        {
            RelativeLocation = new(11, -4, 7),
            RelativeRotation = new(5, 30, 0),
            RelativeScale = new(1.25, 1, 0.75),
            Visible = false,
            HiddenInGame = true
        };
        var leaf = Scene("Leaf", middle.Name);
        var count = new BlueprintRecoveryVariable("Count", "IntProperty", "", 1, 7, BlueprintDefaultStatus.Serialized, "fixture");
        var variables = new[] { count, ComponentVariable(root.Name), ComponentVariable(middle.Name), ComponentVariable(leaf.Name) };

        var plan = BlueprintRecovery.CreateReconstruction(Actor([root, middle, leaf], variables));

        Assert.Empty(plan.Omissions);
        Assert.Equal("Count", Assert.Single(plan.Variables).Name);
        Assert.NotNull(plan.Components);
        Assert.Collection(plan.Components,
            part => { Assert.True(part.IsDefaultSceneRoot); Assert.Null(part.Parent); },
            part =>
            {
                Assert.Equal("DefaultSceneRoot", part.Parent);
                Assert.Equal(middle.RelativeLocation, part.RelativeLocation);
                Assert.Equal(middle.RelativeRotation, part.RelativeRotation);
                Assert.Equal(middle.RelativeScale, part.RelativeScale);
                Assert.False(part.Visible);
                Assert.True(part.HiddenInGame);
            },
            part => Assert.Equal("Pivot", part.Parent));
    }

    [Fact]
    public void Project_reports_unsupported_attachment_and_does_not_reparent_orphans()
    {
        var root = Scene("DefaultSceneRoot", null, isDefault: true);
        var socketChild = Scene("SocketChild", root.Name) with { Socket = "Lamp" };
        var leaf = Scene("Orphan", socketChild.Name);
        var unsupported = Scene("Collision", root.Name) with { ClassPath = "/Script/Engine.SphereComponent" };
        var source = Actor([root, socketChild, leaf, unsupported], []) with { ComponentIssues = ["Partial component evidence."] };

        var plan = BlueprintRecovery.CreateReconstruction(source);

        Assert.NotNull(plan.Components);
        Assert.Equal(root.Name, Assert.Single(plan.Components).Name);
        Assert.Contains(plan.Omissions, item => item.Contains("SocketChild") && item.Contains("socket"));
        Assert.Contains(plan.Omissions, item => item.Contains("Orphan") && item.Contains("attachment parent"));
        Assert.Contains(plan.Omissions, item => item.Contains("SphereComponent"));
        Assert.Contains("Partial component evidence.", plan.Omissions);
    }

    [Theory]
    [InlineData("/Game/BPAudit/SM_PinFixture.SM_PinFixture")]
    [InlineData("/Engine/BasicShapes/Cube.Cube")]
    public void Project_keeps_the_canonical_mesh_and_socket_on_its_local_child(string meshPath)
    {
        var root = Scene("DefaultSceneRoot", null, isDefault: true);
        var mount = Scene("MeshMount", root.Name) with
        {
            ClassPath = BlueprintRecovery.StaticMeshComponentClass,
            StaticMesh = meshPath
        };
        var child = Scene("SocketMarker", mount.Name) with { Socket = "PinSocket" };
        var variable = ComponentVariable(mount.Name) with { TypeReference = BlueprintRecovery.StaticMeshComponentClass };

        var plan = BlueprintRecovery.CreateReconstruction(Actor([root, mount, child], [variable]));

        Assert.Empty(plan.Variables);
        Assert.Empty(plan.Omissions);
        Assert.NotNull(plan.Components);
        Assert.Collection(plan.Components,
            part => Assert.True(part.IsDefaultSceneRoot),
            part =>
            {
                Assert.Equal(BlueprintRecovery.StaticMeshComponentClass, part.ClassPath);
                Assert.Equal(meshPath, part.StaticMesh);
                Assert.Null(part.Socket);
            },
            part =>
            {
                Assert.Equal(mount.Name, part.Parent);
                Assert.Equal("PinSocket", part.Socket);
                Assert.Null(part.StaticMesh);
            });
    }

    [Theory]
    [InlineData("Project/Content/BPAudit/SM_PinFixture.SM_PinFixture")]
    [InlineData("/Game/../SM_Probe.SM_Probe")]
    [InlineData("/Game/BPAudit/SM_Probe.SM_Probe:Nested")]
    [InlineData("/Game/BPAudit/SM_Probe.uasset:SM_Probe")]
    [InlineData("/UnknownPlugin/BPAudit/SM_Probe.SM_Probe")]
    public void Project_rejects_unverified_mesh_paths_and_dependent_socket_children(string archivePath)
    {
        var root = Scene("DefaultSceneRoot", null, isDefault: true);
        var mount = Scene("MeshMount", root.Name) with { ClassPath = BlueprintRecovery.StaticMeshComponentClass, StaticMesh = archivePath };
        var child = Scene("SocketMarker", mount.Name) with { Socket = "PinSocket" };

        var plan = BlueprintRecovery.CreateReconstruction(Actor([root, mount, child], []));

        Assert.NotNull(plan.Components);
        Assert.Equal(2, plan.Components.Count);
        Assert.Null(plan.Components[1].StaticMesh);
        Assert.Contains(plan.Omissions, item => item.Contains("StaticMesh object path"));
        Assert.Contains(plan.Omissions, item => item.Contains("socket"));
    }

    private static BlueprintRecoveryClass Actor(IReadOnlyList<BlueprintRecoveryComponent> components,
        IReadOnlyList<BlueprintRecoveryVariable> variables) => new("Game/Fixture.uasset", "BP_Fixture_C",
            "Game/Fixture.BP_Fixture_C", BlueprintRecovery.ActorParent, variables, [], null, true)
        {
            Components = components
        };

    private static BlueprintRecoveryComponent Scene(string name, string? parent, bool isDefault = false) =>
        new(name, BlueprintRecovery.SceneComponentClass, parent, isDefault, "Fixture:" + name + "_GEN_VARIABLE");

    private static BlueprintRecoveryVariable ComponentVariable(string name) =>
        new(name, "ObjectProperty", "", 1, null, BlueprintDefaultStatus.UnsupportedType, "fixture")
        {
            TypeReference = BlueprintRecovery.SceneComponentClass
        };
}
