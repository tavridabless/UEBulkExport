using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UEBulkExport.Gui.Localization;
using UEBulkExport.Gui.Services;

namespace UEBulkExport.Gui.ViewModels;

public sealed partial class AboutViewModel : ObservableObject
{
    public string Version => Cli.Version;
    public string Runtime => System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;

    public const string ProjectUrl = "https://github.com/tavridabless/UEBulkExport";
    public const string MappingsUrl = "https://github.com/tavridabless/UEBulkExport/blob/main/docs/mappings.md";
    public const string MappingsUrlRu = "https://github.com/tavridabless/UEBulkExport/blob/main/docs/mappings.ru.md";
    public const string IssuesUrl = "https://github.com/tavridabless/UEBulkExport/issues";
    public const string Cue4ParseUrl = "https://github.com/FabianFG/CUE4Parse";
    public const string RetocUrl = "https://github.com/trumank/retoc";

    [RelayCommand] private void OpenProject() => ShellHelper.OpenUrl(ProjectUrl);
    // The guide opens in the interface language.
    [RelayCommand] private void OpenMappings() =>
        ShellHelper.OpenUrl(Loc.Instance.Language == "ru" ? MappingsUrlRu : MappingsUrl);
    [RelayCommand] private void OpenIssues() => ShellHelper.OpenUrl(IssuesUrl);
    [RelayCommand] private void OpenCue4Parse() => ShellHelper.OpenUrl(Cue4ParseUrl);
    [RelayCommand] private void OpenRetoc() => ShellHelper.OpenUrl(RetocUrl);

    [RelayCommand]
    private void OpenNotices()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.md");
        if (File.Exists(path)) ShellHelper.OpenFile(path);
        else ShellHelper.OpenUrl(ProjectUrl + "/blob/main/THIRD-PARTY-NOTICES.md");
    }
}
