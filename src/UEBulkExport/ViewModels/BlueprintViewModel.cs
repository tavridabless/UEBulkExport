using System.Globalization;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UEBulkExport.Gui.Localization;
using UEBulkExport.Gui.Services;

namespace UEBulkExport.Gui.ViewModels;

public sealed record BlueprintFunctionRow(string Name, string ClassName, string Path, string Coverage,
    string? Note, IReadOnlyList<string> Instructions, IReadOnlyList<string> ControlFlow, IReadOnlyList<string> Values)
{
    public string Title => $"{ClassName}.{Name}";
    public override string ToString() => $"{ClassName}.{Name}";
}

public sealed record BlueprintClassRow(BlueprintRecoveryClass Source, IReadOnlyList<BlueprintFunctionRow> Functions,
    IReadOnlyList<string> Values)
{
    public string Name => Source.Name;
    public string Path => Source.Path;
    public override string ToString() => Name;
}

public sealed partial class BlueprintViewModel : ObservableObject, IDisposable
{
    private static Loc L => Loc.Instance;
    private readonly Func<Options> _readExportSource;
    private readonly Func<Options, CancellationToken, BlueprintRecoveryReport> _inspect;
    private CancellationTokenSource? _operation;
    private BlueprintRecoveryReport? _report;
    private Options? _inspectedSource;
    private bool _disposed;
    private string? _oodlePath;
    private string? _zlibPath;
    private ETexturePlatform _platform = ETexturePlatform.DesktopMobile;

    public IReadOnlyList<GameOption> Games { get; }
    public Func<Task<string?>>? PickReportPathAsync { get; set; }
    public Func<Task<string?>>? PickReconstructionPathAsync { get; set; }
    public Func<Task<string?>>? PickBatchReconstructionPathAsync { get; set; }

    [ObservableProperty] private string _paksPath = "";
    [ObservableProperty] private string _packagePath = "";
    [ObservableProperty] private GameOption _game;
    [ObservableProperty] private string _usmapPath = "";
    [ObservableProperty] private string _aesText = "";
    [ObservableProperty] private bool _advancedOpen;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = L["Blueprint.Idle"];
    [ObservableProperty] private string _error = "";
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private IReadOnlyList<BlueprintClassRow> _classes = [];
    [ObservableProperty] private BlueprintClassRow? _selectedClass;
    [ObservableProperty] private IReadOnlyList<BlueprintFunctionRow> _visibleFunctions = [];
    [ObservableProperty] private BlueprintFunctionRow? _selectedFunction;
    [ObservableProperty] private IReadOnlyList<string> _diagnostics = [];
    [ObservableProperty] private int _resultTabIndex;

    public bool CanEdit => !_disposed && !IsBusy;
    public bool CanAnalyze => CanEdit && !string.IsNullOrWhiteSpace(PaksPath);
    public bool CanCancel => !_disposed && _operation is { IsCancellationRequested: false };
    public bool CanSave => CanEdit && HasReport;
    public bool CanSaveReconstruction => CanEdit && SelectedClass is not null;
    public bool HasReport => _report is not null;
    public bool HasError => Error.Length > 0;
    public bool HasClasses => Classes.Count > 0;
    public bool HasFunctions => SelectedClass?.Functions.Count > 0;
    public bool HasVisibleFunctions => VisibleFunctions.Count > 0;
    public bool HasSelectedFunction => SelectedFunction is not null;
    public bool HasDiagnostics => Diagnostics.Count > 0;
    public string Summary => _report is { } report ? L.Format("Blueprint.Summary", report.PackagesInspected,
        report.PackagesSelected, report.Classes.Count, report.InstructionsReported) : "";
    public string CoverageWarning => _report is { } report ? CoverageOf(report) : "";
    public bool HasCoverageWarning => CoverageWarning.Length > 0;
    public IReadOnlyList<string> DetailValues { get; private set; } = [];

    public BlueprintViewModel(ExportViewModel export) : this(export.Games, export.ToOptions)
    {
        var current = export.ToOptions();
        _oodlePath = current.OodlePath;
        _zlibPath = current.ZlibPath;
        _platform = current.Platform;
        _game = export.SelectedGame;
    }

    internal BlueprintViewModel(IReadOnlyList<GameOption> games, Func<Options> readExportSource,
        Func<Options, CancellationToken, BlueprintRecoveryReport>? inspect = null)
    {
        Games = games;
        _readExportSource = readExportSource;
        _inspect = inspect ?? ((source, ct) => BlueprintRecovery.Inspect(source, cancellationToken: ct));
        _game = games[0];
    }

    /// <summary>Copies the current source profile into this workspace without starting an analysis.</summary>
    public void UseExportSource(string? packagePath = null)
    {
        if (!CanEdit) return;
        var source = _readExportSource();
        var sameSource = string.Equals(PaksPath.Trim(), source.PaksDirectory, StringComparison.OrdinalIgnoreCase);
        var packages = source.SelectedPaths?.Where(IsPackagePath).Take(2).ToArray();
        PaksPath = source.PaksDirectory;
        PackagePath = packagePath ?? (packages is { Length: 1 } ? packages[0] : sameSource ? PackagePath : "");
        if (Games.FirstOrDefault(game => game.Value == source.Game) is { } profile) Game = profile;
        UsmapPath = source.UsmapPath ?? "";
        AesText = string.Join("; ", source.AesKeys);
        _oodlePath = source.OodlePath;
        _zlibPath = source.ZlibPath;
        _platform = source.Platform;
        ClearReport();
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void UseExport() => UseExportSource();

    [RelayCommand]
    private async Task BrowseFolder()
    {
        if (await DialogService.PickFolderAsync("Blueprint.Source.Pick", PaksPath) is { } path && CanEdit)
            PaksPath = path;
    }

    [RelayCommand]
    private async Task BrowseContainer()
    {
        if (await DialogService.PickContainerAsync(PaksPath) is { } path && CanEdit) PaksPath = path;
    }

    [RelayCommand]
    private async Task BrowseUsmap()
    {
        var start = string.IsNullOrWhiteSpace(UsmapPath) ? PaksPath : UsmapPath;
        if (await DialogService.PickUsmapAsync(start) is { } path && CanEdit) UsmapPath = path;
    }

    [RelayCommand(CanExecute = nameof(CanAnalyze))]
    private async Task Analyze()
    {
        if (!CanAnalyze) return;
        ClearReport();
        using var cancellation = new CancellationTokenSource();
        _operation = cancellation;
        IsBusy = true;
        StatusText = L["Blueprint.Running"];
        try
        {
            var options = BuildOptions();
            var (result, classes, diagnostics, dependencyIndex) = await Task.Run(() =>
            {
                var report = _inspect(options, cancellation.Token);
                var rows = report.Classes.Select(type => FormatClass(type, cancellation.Token)).ToArray();
                var messages = report.Diagnostics.Select(diagnostic =>
                    $"{diagnostic.ObjectPath ?? diagnostic.PackagePath}\n{diagnostic.Message}").ToArray();
                var links = report.DependencyMap.Edges.ToLookup(edge => edge.Source, DependencyPaths);
                return (report, rows, messages, links);
            }, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (_disposed) return;

            _report = result;
            _inspectedSource = options;
            _dependencyIndex = dependencyIndex;
            Search = "";
            Classes = classes;
            Diagnostics = diagnostics;
            SelectedClass = classes.FirstOrDefault(row => row.Functions.Count > 0) ?? classes.FirstOrDefault();
            ResultTabIndex = HasFunctions ? 0 : HasDiagnostics ? 3 : 2;
            AdvancedOpen = false;
            StatusText = L["Blueprint.Done"];
            NotifyReportChanged();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!_disposed) StatusText = L["Blueprint.Cancelled"];
        }
        catch (Exception error)
        {
            if (!_disposed) ShowError(error, "Blueprint.Error.Analysis");
        }
        finally
        {
            _operation = null;
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        if (!CanCancel) return;
        _operation!.Cancel();
        StatusText = L["Blueprint.Cancelling"];
        OnPropertyChanged(nameof(CanCancel));
        CancelCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private Task SaveReport() => CanSave && _report is { } report
        ? SaveOutputAsync(PickReportPathAsync, path => BlueprintRecovery.WriteReport(report, path), "Blueprint.Saved")
        : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanSaveReconstruction))]
    private Task SaveReconstruction() => CanSaveReconstruction && SelectedClass is { } row
        ? SaveOutputAsync(PickReconstructionPathAsync, path => BlueprintRecovery.WriteReconstruction(row.Source, path),
            "Blueprint.Reconstruction.Saved")
        : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private Task SaveBatchReconstruction() => CanSave && _report is { } report
        ? SaveOutputAsync(PickBatchReconstructionPathAsync,
            path => BlueprintRecovery.WriteBatchReconstruction(report, path),
            result => result.ClassesIncluded == 0
                ? L.Format("Blueprint.Batch.CoverageOnly", result.ClassesInspected)
                : L.Format("Blueprint.Batch.Saved", result.ClassesIncluded, result.ClassesInspected, result.ClassesExcluded,
                    result.FunctionsReconstructed, result.FunctionsStubbed, result.FunctionsOmitted, result.EventsReconstructed))
        : Task.CompletedTask;

    private Task SaveOutputAsync(Func<Task<string?>>? pick, Action<string> write, string successKey) =>
        SaveOutputAsync(pick, path => { write(path); return successKey; }, key => L[key]);

    private async Task SaveOutputAsync<TResult>(Func<Task<string?>>? pick, Func<string, TResult> write,
        Func<TResult, string> describeSuccess)
    {
        if (pick is null) return;
        IsBusy = true;
        try
        {
            var path = await pick();
            if (path is null || _disposed) return;
            StatusText = L["Blueprint.Saving"];
            Error = "";
            var result = await Task.Run(() => write(path));
            if (!_disposed) StatusText = describeSuccess(result);
        }
        catch (Exception error)
        {
            if (!_disposed) ShowError(error, "Blueprint.Error.Output");
        }
        finally
        {
            IsBusy = false;
        }
    }

    internal Options BuildOptions()
    {
        var target = PackagePath.Trim().Replace('\\', '/');
        if (target.Length > 0 && !IsPackagePath(target))
            throw new UserFacingException(L["Blueprint.Package.Invalid"]);
        return new Options
        {
            PaksDirectory = PaksPath.Trim(),
            Mode = ExportMode.List,
            Game = Game.Value,
            Platform = _platform,
            AesKeys = AesText.Split(['\r', '\n', ';'],
                StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList(),
            UsmapPath = string.IsNullOrWhiteSpace(UsmapPath) ? null : UsmapPath.Trim(),
            OodlePath = _oodlePath,
            ZlibPath = _zlibPath,
            SelectedPaths = target.Length == 0 ? null : new HashSet<string>([target], StringComparer.OrdinalIgnoreCase)
        };
    }

    private static bool IsPackagePath(string path) => path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".umap", StringComparison.OrdinalIgnoreCase);

    private static BlueprintClassRow FormatClass(BlueprintRecoveryClass type, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var fields = new List<string> { L.Format("Blueprint.Values.Parent", type.Parent ?? L["Blueprint.Unknown"]) };
        if (!type.MetadataComplete) fields.Add(L["Blueprint.Coverage.Metadata"]);
        fields.AddRange(type.Variables.Select(variable =>
            $"{DescribeValue(variable.Name, variable.Type, variable.ArrayDimension)} · " +
            $"{L[$"Blueprint.Default.{variable.DefaultStatus}"]}" +
            (variable.DefaultValue is { } value ? $" = {FormatScalar(value)}" : "")));
        var functions = type.Functions.Select(function => FormatFunction(type.Name, function, ct)).ToArray();
        return new BlueprintClassRow(type, functions, fields);
    }

    private static BlueprintFunctionRow FormatFunction(string className, BlueprintRecoveryFunction function,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var instructionLines = new List<string>();
        var pending = new Stack<(BlueprintRecoveryInstruction Instruction, int Depth)>();
        PushOperands(function.Instructions, 0);
        while (pending.TryPop(out var next))
        {
            ct.ThrowIfCancellationRequested();
            var (instruction, depth) = next;
            var line = $"0x{instruction.Offset:X4}  {new string(' ', Math.Min(depth, 12) * 2)}{instruction.Opcode}";
            if (instruction.Reference is { } reference) line += $"  {reference}";
            if (instruction.Value is { } value) line += $"  = {FormatScalar(value)}";
            if (instruction.TargetOffset is { } target) line += $"  → 0x{target:X4}";
            instructionLines.Add(line);
            PushOperands(instruction.Operands, depth + 1);
        }

        var flow = function.ControlFlow.Select(edge =>
            $"0x{edge.FromOffset:X4}  →  {(edge.ToOffset is { } target ? $"0x{target:X4}" : "—")}  {edge.Kind}" +
            (edge.ToOffset is not null && !edge.TargetKnown ? $" · {L["Blueprint.Flow.Unresolved"]}" : "")).ToArray();
        var values = new List<string>();
        if (!function.ParametersComplete) values.Add(L["Blueprint.Coverage.Parameters"]);
        values.AddRange(function.Parameters.Select(parameter =>
            $"{L[$"Blueprint.Direction.{parameter.Direction}"]} {DescribeValue(parameter.Name, parameter.Type, parameter.ArrayDimension)}"));
        var coverage = L.Format("Blueprint.Function.Status", L[$"Blueprint.Bytecode.{function.BytecodeStatus}"],
            instructionLines.Count);
        return new BlueprintFunctionRow(function.Name, className, function.Path, coverage, function.Diagnostic,
            instructionLines, flow, values);

        void PushOperands(IReadOnlyList<BlueprintRecoveryInstruction> operands, int depth)
        {
            for (var i = operands.Count - 1; i >= 0; i--) pending.Push((operands[i], depth));
        }
    }

    private static string DescribeValue(string name, string type, int dimension) => dimension > 1
        ? $"{name}: {type}[{dimension}]" : $"{name}: {type}";

    private static string FormatScalar(object value) => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";

    private static string CoverageOf(BlueprintRecoveryReport report)
    {
        var lines = new List<string>();
        if (report.PackageLimitReached) lines.Add(L["Blueprint.Coverage.Packages"]);
        if (report.InstructionLimitReached) lines.Add(L["Blueprint.Coverage.Instructions"]);
        if (report.MetadataLimitReached) lines.Add(L["Blueprint.Coverage.Metadata"]);
        if (report.PackagesSkipped > 0 || report.PackagesFailed > 0)
            lines.Add(L.Format("Blueprint.Coverage.Skipped", report.PackagesSkipped, report.PackagesFailed));
        if (report.LockedContainers > 0)
            lines.Add(L.Format("Blueprint.Coverage.Locked", report.LockedContainers));
        return string.Join(" ", lines);
    }

    private void ShowError(Exception error, string statusKey)
    {
        StatusText = L[statusKey];
        Error = error is UserFacingException message
            ? string.Join(Environment.NewLine, new[] { message.Headline, message.Hint }.OfType<string>())
            : L["Error.Unexpected"];
        if (error is not UserFacingException) Log.Error(error.ToString());
    }

    private void ClearReport()
    {
        _report = null;
        _inspectedSource = null;
        _dependencyIndex = null;
        SelectedClass = null;
        Classes = [];
        VisibleFunctions = [];
        Diagnostics = [];
        SelectedFunction = null;
        Error = "";
        StatusText = L[string.IsNullOrWhiteSpace(PaksPath) ? "Blueprint.Idle" : "Blueprint.Ready"];
        NotifyReportChanged();
    }

    private void NotifyReportChanged()
    {
        OnPropertyChanged(nameof(HasReport));
        OnPropertyChanged(nameof(HasClasses));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(CoverageWarning));
        OnPropertyChanged(nameof(HasCoverageWarning));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(CanRecoverProject));
        SaveReportCommand.NotifyCanExecuteChanged();
        SaveBatchReconstructionCommand.NotifyCanExecuteChanged();
        RecoverProjectCommand.NotifyCanExecuteChanged();
        NotifyLinkedReconstructionAvailability();
    }

    private void ApplySearch()
    {
        var query = Search.Trim();
        var functions = SelectedClass?.Functions ?? [];
        VisibleFunctions = query.Length == 0 ? functions : functions.Where(row =>
            row.Path.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (SelectedFunction is null || !VisibleFunctions.Contains(SelectedFunction))
            SelectedFunction = VisibleFunctions.FirstOrDefault();
    }

    partial void OnPaksPathChanged(string value)
    {
        PackagePath = "";
        ClearReport();
        OnPropertyChanged(nameof(CanAnalyze));
        AnalyzeCommand.NotifyCanExecuteChanged();
    }

    partial void OnPackagePathChanged(string value) => ClearReport();
    partial void OnGameChanged(GameOption value) => ClearReport();
    partial void OnUsmapPathChanged(string value) => ClearReport();
    partial void OnAesTextChanged(string value) => ClearReport();
    partial void OnSearchChanged(string value) => ApplySearch();
    partial void OnErrorChanged(string value) => OnPropertyChanged(nameof(HasError));
    partial void OnDiagnosticsChanged(IReadOnlyList<string> value) => OnPropertyChanged(nameof(HasDiagnostics));
    partial void OnVisibleFunctionsChanged(IReadOnlyList<BlueprintFunctionRow> value) =>
        OnPropertyChanged(nameof(HasVisibleFunctions));

    partial void OnSelectedClassChanged(BlueprintClassRow? value)
    {
        ApplySearch();
        RefreshDetailValues();
        RefreshDependencyLinks();
        OnPropertyChanged(nameof(HasFunctions));
        OnPropertyChanged(nameof(CanSaveReconstruction));
        SaveReconstructionCommand.NotifyCanExecuteChanged();
        NotifyLinkedReconstructionAvailability();
    }

    partial void OnSelectedFunctionChanged(BlueprintFunctionRow? value)
    {
        RefreshDetailValues();
        OnPropertyChanged(nameof(HasSelectedFunction));
    }

    private void RefreshDetailValues()
    {
        DetailValues = [.. SelectedFunction?.Values ?? [], .. SelectedClass?.Values ?? []];
        OnPropertyChanged(nameof(DetailValues));
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(CanAnalyze));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(CanSaveReconstruction));
        OnPropertyChanged(nameof(CanRecoverProject));
        AnalyzeCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        SaveReportCommand.NotifyCanExecuteChanged();
        SaveReconstructionCommand.NotifyCanExecuteChanged();
        SaveBatchReconstructionCommand.NotifyCanExecuteChanged();
        RecoverProjectCommand.NotifyCanExecuteChanged();
        NotifyLinkedReconstructionAvailability();
        UseExportCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _operation?.Cancel();
        PickReportPathAsync = null;
        PickReconstructionPathAsync = null;
        PickLinkedReconstructionPathAsync = null;
        PickBatchReconstructionPathAsync = null;
    }
}
