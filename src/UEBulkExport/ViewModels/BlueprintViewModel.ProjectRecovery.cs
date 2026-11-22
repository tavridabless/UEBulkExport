using CommunityToolkit.Mvvm.Input;
using UEBulkExport.Gui.Services;

namespace UEBulkExport.Gui.ViewModels;

public sealed partial class BlueprintViewModel
{
    internal Func<Task<(string Editor, string Output)?>> PickProjectRecoveryPathsAsync { get; set; } = PickRecoveryPathsAsync;
    internal Func<BlueprintRecoveryReport, Options, string, string, CancellationToken, Task<BlueprintProjectRecoveryResult>>
        RecoverProjectAsync { get; set; } = BlueprintProjectRecovery.RecoverAsync;

    public bool CanRecoverProject => OperatingSystem.IsWindows() && CanSave && HasClasses && _inspectedSource is not null;

    [RelayCommand(CanExecute = nameof(CanRecoverProject))]
    private async Task RecoverProject()
    {
        if (!CanRecoverProject || _report is not { } report || _inspectedSource is not { } source) return;
        using var cancellation = new CancellationTokenSource();
        _operation = cancellation;
        IsBusy = true;
        try
        {
            var paths = await PickProjectRecoveryPathsAsync();
            cancellation.Token.ThrowIfCancellationRequested();
            if (paths is not { } selected || _disposed) return;
            Error = "";
            StatusText = L["Blueprint.ProjectRecovery.Running"];
            var result = await Task.Run(() => RecoverProjectAsync(report, source, selected.Editor, selected.Output, cancellation.Token),
                cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (_disposed) return;
            StatusText = result.ProjectPath is not { } project
                ? L.Format("Blueprint.ProjectRecovery.CoverageOnly", result.OutputDirectory)
                : L.Format(result.HasGaps ? "Blueprint.ProjectRecovery.Partial" : "Blueprint.ProjectRecovery.Done", project);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!_disposed) StatusText = L["Blueprint.ProjectRecovery.Cancelled"];
        }
        catch (Exception error)
        {
            if (!_disposed) ShowError(error, "Blueprint.ProjectRecovery.Error");
        }
        finally
        {
            _operation = null;
            IsBusy = false;
        }
    }

    private static async Task<(string Editor, string Output)?> PickRecoveryPathsAsync()
    {
        if (await DialogService.PickUnrealEditorForRecoveryAsync() is not { } editor) return null;
        if (!File.Exists(editor) || !Path.GetFileName(editor).Equals("UnrealEditor-Cmd.exe", StringComparison.OrdinalIgnoreCase))
            throw new UserFacingException(L["Blueprint.ProjectRecovery.InvalidEditor"]);
        if (await DialogService.PickFolderAsync("Blueprint.ProjectRecovery.PickOutput") is not { } output) return null;
        return (editor, output);
    }
}
