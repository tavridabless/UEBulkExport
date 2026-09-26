using Avalonia.Controls;
using Avalonia.Platform.Storage;
using UEBulkExport.Gui.Localization;

namespace UEBulkExport.Gui.Services;

/// <summary>File and folder pickers, bound to the main window once it exists.</summary>
public static class DialogService
{
    public static TopLevel? Owner { get; set; }

    private static Loc L => Loc.Instance;

    public static async Task<string?> PickFolderAsync(string titleKey, string? startPath = null)
    {
        if (Owner?.StorageProvider is not { } storage) return null;

        var options = new FolderPickerOpenOptions
        {
            Title = L[titleKey],
            AllowMultiple = false,
            SuggestedStartLocation = await TryFolderAsync(storage, startPath)
        };

        var result = await storage.OpenFolderPickerAsync(options);
        return result.FirstOrDefault()?.TryGetLocalPath();
    }

    public static Task<string?> PickContainerAsync(string? startPath = null) =>
        PickFileAsync("Dialog.PickContainer", startPath,
            new FilePickerFileType(L["Dialog.Filter.Containers"]) { Patterns = ["*.utoc", "*.pak", "*.ucas"] });

    public static Task<string?> PickUsmapAsync(string? startPath = null) =>
        PickFileAsync("Dialog.PickUsmap", startPath,
            new FilePickerFileType(L["Dialog.Filter.Usmap"]) { Patterns = ["*.usmap"] });

    public static Task<string?> PickUnrealProjectAsync(string? startPath = null) =>
        PickFileAsync("Dialog.PickProject", startPath,
            new FilePickerFileType(L["Dialog.Filter.Projects"]) { Patterns = ["*.uproject"] });

    public static Task<string?> PickExecutableAsync(string? startPath = null) =>
        PickFileAsync("Dialog.PickExe", startPath,
            new FilePickerFileType(L["Dialog.Filter.Executables"]) { Patterns = OperatingSystem.IsWindows() ? ["*.exe"] : ["*"] });

    public static Task<string?> PickLibraryAsync(string? startPath = null) =>
        PickFileAsync("Dialog.PickLibrary", startPath,
            new FilePickerFileType(L["Dialog.Filter.Libraries"]) { Patterns = ["*.dll", "*.so", "*.dylib"] });

    private static async Task<string?> PickFileAsync(string titleKey, string? startPath, FilePickerFileType type)
    {
        if (Owner?.StorageProvider is not { } storage) return null;

        var options = new FilePickerOpenOptions
        {
            Title = L[titleKey],
            AllowMultiple = false,
            FileTypeFilter = [type, new FilePickerFileType(L["Dialog.Filter.All"]) { Patterns = ["*"] }],
            SuggestedStartLocation = await TryFolderAsync(storage, startPath)
        };

        var result = await storage.OpenFilePickerAsync(options);
        return result.FirstOrDefault()?.TryGetLocalPath();
    }

    private static async Task<IStorageFolder?> TryFolderAsync(IStorageProvider storage, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        try
        {
            var folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
            if (folder is null || !Directory.Exists(folder)) return null;
            return await storage.TryGetFolderFromPathAsync(folder);
        }
        catch
        {
            return null;
        }
    }
}
