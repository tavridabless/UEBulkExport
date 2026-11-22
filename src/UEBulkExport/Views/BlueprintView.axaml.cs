using Avalonia.Controls;
using Avalonia.Platform.Storage;
using UEBulkExport.Gui.Localization;
using UEBulkExport.Gui.ViewModels;

namespace UEBulkExport.Gui.Views;

public sealed partial class BlueprintView : UserControl
{
    public BlueprintView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not BlueprintViewModel model) return;
            model.PickReportPathAsync = () => PickJsonPathAsync("Blueprint.Output.Pick",
                "Blueprint.Output.FileType", "UEBulkExport.blueprint.json");
            model.PickReconstructionPathAsync = () => PickJsonPathAsync("Blueprint.Reconstruction.Pick",
                "Blueprint.Reconstruction.FileType", "UEBulkExport.blueprint-plan.json");
            model.PickLinkedReconstructionPathAsync = () => PickJsonPathAsync("Blueprint.Dependencies.Pick",
                "Blueprint.Dependencies.FileType", "UEBulkExport.blueprint-linked-plan.json");
            model.PickBatchReconstructionPathAsync = () => PickJsonPathAsync("Blueprint.Batch.Pick",
                "Blueprint.Batch.FileType", "UEBulkExport.blueprint-batch-plan.json");
        };
    }

    private async Task<string?> PickJsonPathAsync(string title, string fileType, string suggestedName)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return null;
        var language = Loc.Instance;
        var result = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = language[title],
            SuggestedFileName = suggestedName,
            DefaultExtension = "json",
            FileTypeChoices = [new FilePickerFileType(language[fileType]) { Patterns = ["*.json"] }],
            ShowOverwritePrompt = true
        });
        return result?.TryGetLocalPath();
    }
}
