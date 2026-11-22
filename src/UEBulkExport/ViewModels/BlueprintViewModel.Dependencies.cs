using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UEBulkExport.Gui.Localization;

namespace UEBulkExport.Gui.ViewModels;

public sealed record BlueprintDependencyRow(BlueprintDependencyEdge Edge)
{
    public string Target => Edge.Target;
    public string TargetLabel => Edge.Resolution == BlueprintDependencyResolution.Unknown && Target == "<unresolved>"
        ? Loc.Instance["Blueprint.Dependencies.Resolution.Unknown"] : Target;
    public string Relation => Loc.Instance[Edge.IsRuntimeDispatch ? "Blueprint.Dependencies.Kind.RuntimeCall" :
        $"Blueprint.Dependencies.Kind.{Edge.Kind}"];
    public string Resolution => Loc.Instance[$"Blueprint.Dependencies.Resolution.{Edge.Resolution}"];
    public bool IsInspected => Edge.Resolution == BlueprintDependencyResolution.Inspected;
    public string Context => FormatContext(Edge.Source[(Math.Max(Edge.Source.LastIndexOf('/'),
        Edge.Source.LastIndexOf('.')) + 1)..]);
    public string FullContext => FormatContext(Edge.Source);

    private string FormatContext(string source)
    {
        if (Edge.SourceFunction is { } function) source += $" · {function}";
        if (Edge.Member is { } member) source += $" · {member}";
        if (Edge.Offset is { } offset) source += $" · 0x{offset:X4}";
        return source;
    }

    public override string ToString() => $"{Relation} · {TargetLabel} · {Resolution}";
}

public sealed partial class BlueprintViewModel
{
    private const int DependenciesTab = 4;
    private static readonly StringComparer DependencyPaths = StringComparer.OrdinalIgnoreCase;
    private ILookup<string, BlueprintDependencyEdge>? _dependencyIndex;

    public Func<Task<string?>>? PickLinkedReconstructionPathAsync { get; set; }
    [ObservableProperty] private bool _includeTransitiveLinks;
    [ObservableProperty] private BlueprintDependencyRow? _selectedDependency;
    [ObservableProperty] private IReadOnlyList<BlueprintDependencyRow> _dependencyLinks = [];

    public bool HasDependencyLinks => DependencyLinks.Count > 0;
    public string DependencySummary => L.Format("Blueprint.Dependencies.Summary", DependencyLinks.Count,
        DependencyLinks.Where(item => item.IsInspected).Select(item => item.Target).Distinct(DependencyPaths).Count());
    public bool CanOpenDependency => FindTargetClass() is not null;
    public bool CanSaveLinkedReconstruction => CanSaveReconstruction && HasReport;

    [RelayCommand(CanExecute = nameof(CanSaveLinkedReconstruction))]
    private Task SaveLinkedReconstruction() => CanSaveLinkedReconstruction && _report is { } report && SelectedClass is { } row
        ? SaveOutputAsync(PickLinkedReconstructionPathAsync,
            path => BlueprintRecovery.WriteLinkedReconstruction(report, row.Path, path), "Blueprint.Dependencies.Saved")
        : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanOpenDependency))]
    private void OpenDependency()
    {
        if (FindTargetClass() is not { } target) return;
        Search = "";
        SelectedClass = target;
    }

    private BlueprintClassRow? FindTargetClass() =>
        SelectedDependency is { IsInspected: true } link &&
        !DependencyPaths.Equals(link.Target, SelectedClass?.Path)
            ? Classes.FirstOrDefault(candidate => DependencyPaths.Equals(candidate.Path, link.Target)) : null;

    private void RefreshDependencyLinks()
    {
        SelectedDependency = null;
        DependencyLinks = ResultTabIndex == DependenciesTab && _dependencyIndex is { } index && SelectedClass is { } row
            ? CollectDependencyEdges(index, row.Path, IncludeTransitiveLinks).Select(edge => new BlueprintDependencyRow(edge)).ToArray()
            : [];
        OnPropertyChanged(nameof(HasDependencyLinks));
        OnPropertyChanged(nameof(DependencySummary));
    }

    internal static IReadOnlyList<BlueprintDependencyEdge> CollectDependencyEdges(
        ILookup<string, BlueprintDependencyEdge> index, string root, bool includeTransitive)
    {
        if (!includeTransitive) return index[root].ToArray();

        var visited = new HashSet<string>(DependencyPaths) { root };
        var pending = new Queue<string>();
        var edges = new List<BlueprintDependencyEdge>();
        pending.Enqueue(root);
        while (pending.TryDequeue(out var current))
        {
            foreach (var edge in index[current])
            {
                edges.Add(edge);
                if (edge.Resolution == BlueprintDependencyResolution.Inspected && visited.Add(edge.Target))
                    pending.Enqueue(edge.Target);
            }
        }
        return edges;
    }

    partial void OnIncludeTransitiveLinksChanged(bool value) => RefreshDependencyLinks();
    partial void OnResultTabIndexChanged(int value) => RefreshDependencyLinks();

    partial void OnSelectedDependencyChanged(BlueprintDependencyRow? value)
    {
        OnPropertyChanged(nameof(CanOpenDependency));
        OpenDependencyCommand.NotifyCanExecuteChanged();
    }

    private void NotifyLinkedReconstructionAvailability()
    {
        OnPropertyChanged(nameof(CanSaveLinkedReconstruction));
        SaveLinkedReconstructionCommand.NotifyCanExecuteChanged();
    }
}
