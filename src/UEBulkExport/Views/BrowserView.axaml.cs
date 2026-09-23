using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using UEBulkExport.Gui.ViewModels;

namespace UEBulkExport.Gui.Views;

public sealed partial class BrowserView : UserControl
{
    public BrowserView()
    {
        InitializeComponent();
    }

    private void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is BrowserViewModel vm && vm.FocusedRow is { IsFolder: true } row)
            vm.OpenRowCommand.Execute(row);
    }

    private async void OnCopyPath(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not BrowserViewModel vm || string.IsNullOrEmpty(vm.DetailPath)) return;
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(vm.DetailPath);
    }
}
