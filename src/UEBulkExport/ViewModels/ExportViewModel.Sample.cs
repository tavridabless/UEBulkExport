using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UEBulkExport.Gui.Localization;
using UEBulkExport.Gui.Services;

namespace UEBulkExport.Gui.ViewModels;

public sealed record SampleTypeOption(AssetSampleType? Type)
{
    public string Label => Loc.Instance[$"Export.Sample.Type.{Type?.ToString() ?? "All"}"];
    public override string ToString() => Label;
}

public sealed partial class ExportViewModel
{
    private static readonly SampleTypeOption[] SampleChoices =
        [new((AssetSampleType?)null), .. Enum.GetValues<AssetSampleType>().Select(type => new SampleTypeOption(type))];
    private AssetSampleResult? _sampleResult;

    [ObservableProperty] private SampleTypeOption _selectedSampleType = SampleChoices[0];
    [ObservableProperty] private int _samplesPerType = 1;
    [ObservableProperty] private string _sampleError = "";

    public IReadOnlyList<SampleTypeOption> SampleTypes => SampleChoices;
    public bool CanSample => CanRun && IsFullMode && !SkipAssets && !string.IsNullOrWhiteSpace(PaksPath);
    public bool HasSample => _sampleResult is not null;
    public string SampleStatus => _sampleResult is not { } result ? "" : L.Format(
        result.Cancelled ? "Export.Sample.Cancelled" : "Export.Sample.Summary",
        result.Coverage.Count > 0 ? result.Coverage.Sum(type => type.Candidates) : result.Items.Count,
        result.PackagesInspected, result.DiscoveryFailures);
    public string SampleCoverage => _sampleResult is not { } result ? "" : string.Join(Environment.NewLine,
        new[]
        {
            result.PackageLimitReached ? L["Export.Sample.Limit"] : "",
            result.OmittedItems == 0 && result.OmittedErrorRows == 0 ? "" :
                L.Format("Export.Sample.Omitted", result.OmittedItems, result.OmittedErrorRows)
        }.Where(message => message.Length > 0));

    public IReadOnlyList<SummaryLine> SampleCoverageRows => _sampleResult?.Coverage.Select(type => new SummaryLine(
        L[$"Export.Sample.Type.{type.Type}"],
        L.Format("Export.Sample.CoverageRow", type.Converted, type.Requested, type.Candidates,
            type.NoOutput, type.Failed, L[$"Export.Sample.Coverage.{type.Status}"]))).ToArray() ?? [];

    public IReadOnlyList<SummaryLine> SampleRows => _sampleResult?.Items.Select(item => new SummaryLine(
        L.Format("Export.Sample.Row", L[$"Export.Sample.Type.{item.Type}"], item.ObjectName),
        L.Format("Export.Sample.RowResult", L[$"Export.Sample.Status.{item.Status}"], item.FilesWritten,
            item.InputPath))).ToArray() ?? [];

    [RelayCommand(CanExecute = nameof(CanSample))]
    private async Task RunSample()
    {
        if (!CanSample) return;
        var options = ToOptions();
        options.PathsFile = null;
        // Use the form's resolved mappings, but keep all test files away from the normal export.
        options.OutputDirectory = "";
        var request = new AssetSampleRequest(SelectedSampleType.Type, SamplesPerType);
        var previousState = State;
        _sampleResult = null;
        SampleError = "";
        RefreshSampleResult();
        State = RunState.Preparing;
        ProgressIndeterminate = true;
        try
        {
            var result = await _service.SampleAsync(options, request);
            if (!_disposed) _sampleResult = result;
        }
        catch (OperationCanceledException)
        {
            if (!_disposed) SampleError = L["Export.Sample.CancelledBeforeReport"];
        }
        catch (UserFacingException e)
        {
            if (!_disposed) SampleError = e.Hint is { Length: > 0 } ? e.Headline + Environment.NewLine + e.Hint : e.Headline;
        }
        catch (Exception e)
        {
            Log.Error(e.ToString());
            if (!_disposed) SampleError = L["Export.Sample.Failed"];
        }
        finally
        {
            if (!_disposed)
            {
                ProgressIndeterminate = false;
                State = previousState;
                RefreshSampleResult();
                RunQueuedScan();
            }
        }
    }

    [RelayCommand]
    private void OpenSampleFolder()
    {
        if (_sampleResult is { } result) ShellHelper.OpenFolder(result.OutputDirectory);
    }

    private void RefreshSampleResult()
    {
        OnPropertyChanged(nameof(HasSample));
        OnPropertyChanged(nameof(SampleStatus));
        OnPropertyChanged(nameof(SampleCoverage));
        OnPropertyChanged(nameof(SampleCoverageRows));
        OnPropertyChanged(nameof(SampleRows));
    }

    private void RefreshSampleAvailability()
    {
        OnPropertyChanged(nameof(CanSample));
        RunSampleCommand.NotifyCanExecuteChanged();
    }
}
