using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using UEBulkExport.Gui.ViewModels;

namespace UEBulkExport.Gui.Views;

public sealed partial class LogView : UserControl
{
    private LogViewModel? _boundModel;
    private bool _followScheduled;
    public LogView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_boundModel?.ScrollToEnd == ScrollToEnd) _boundModel.ScrollToEnd = null;
            _boundModel = DataContext as LogViewModel;
            if (_boundModel is not null) _boundModel.ScrollToEnd = ScrollToEnd;
            ScrollToEnd();
        };
        AttachedToVisualTree += (_, _) => ScrollToEnd();
    }

    private void ScrollToEnd()
    {
        if (_followScheduled) return;
        _followScheduled = true;
        Dispatcher.UIThread.Post(() =>
        {
            _followScheduled = false;
            // Reset notifications and tab activation need a layout pass before scrolling.
            if (DataContext is LogViewModel { AutoScroll: true } && TopLevel.GetTopLevel(this) is not null && List.ItemCount > 0)
                List.ScrollIntoView(List.ItemCount - 1);
        }, DispatcherPriority.Background);
    }

    private async void OnCopyAll(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LogViewModel vm) return;
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(vm.AllText());
    }
}
