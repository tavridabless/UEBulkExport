using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using UEBulkExport.Gui.ViewModels;

namespace UEBulkExport.Gui.Views;

public sealed partial class BrowserView : UserControl
{
    private BrowserViewModel? _boundModel;
    private bool _isAttached;
    private bool? _showingDetails;

    public BrowserView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        SizeChanged += OnSizeChanged;
        AttachedToVisualTree += OnAttached;
        DetachedFromVisualTree += OnDetached;
    }

    internal static bool ShouldShowDetails(double width, bool hasDetail) => width >= 900 || hasDetail;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        BindModel();
        UpdateDetailsLayout();
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e) => UpdateDetailsLayout();

    private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _isAttached = true;
        BindModel();
        UpdateDetailsLayout();
    }

    private void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _isAttached = false;
        BindModel();
    }

    private void BindModel()
    {
        var model = _isAttached ? DataContext as BrowserViewModel : null;
        if (ReferenceEquals(_boundModel, model)) return;
        if (_boundModel is not null) _boundModel.PropertyChanged -= OnModelPropertyChanged;
        _boundModel = model;
        if (_boundModel is not null) _boundModel.PropertyChanged += OnModelPropertyChanged;
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(BrowserViewModel.HasDetail) or null or "") UpdateDetailsLayout();
    }

    private void UpdateDetailsLayout()
    {
        var show = ShouldShowDetails(Bounds.Width, DataContext is BrowserViewModel { HasDetail: true });
        if (_showingDetails == show) return;
        _showingDetails = show;
        // At the main window's minimum size, give filenames the empty details card's 200px.
        // Focusing an entry restores its complete name/path/details; wide windows keep the hint.
        DetailsCard.IsVisible = show;
        BrowserPanes.ColumnDefinitions[3].Width = new GridLength(show ? 10 : 0);
        BrowserPanes.ColumnDefinitions[4].Width = new GridLength(show ? 190 : 0);
    }

    private void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is BrowserViewModel vm && vm.FocusedRow is { IsFolder: true } row)
            vm.OpenRowCommand.Execute(row);
    }

    private async void OnCopyPath(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not BrowserViewModel vm || string.IsNullOrEmpty(vm.DetailPath)) return;
        await vm.CopyFocusedPathAsync(TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard
            ? clipboard.SetTextAsync : null);
    }
}
