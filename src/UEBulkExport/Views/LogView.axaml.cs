using Avalonia.Controls;
using Avalonia.Interactivity;
using UEBulkExport.Gui.ViewModels;

namespace UEBulkExport.Gui.Views;

public sealed partial class LogView : UserControl
{
    public LogView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is LogViewModel vm) vm.ScrollToEnd = ScrollToEnd;
        };
    }

    private void ScrollToEnd()
    {
        if (List.ItemCount > 0) List.ScrollIntoView(List.ItemCount - 1);
    }

    private async void OnCopyAll(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LogViewModel vm) return;
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(vm.AllText());
    }
}
