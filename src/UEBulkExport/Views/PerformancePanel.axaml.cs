using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;
using UEBulkExport.Gui.ViewModels;

namespace UEBulkExport.Gui.Views;

public sealed partial class PerformancePanel : UserControl
{
    private PerformanceViewModel? _viewModel;

    public PerformancePanel()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_viewModel is not null) _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel = DataContext as PerformanceViewModel;
            if (_viewModel is not null) _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        };
    }

    /// <summary>The panel sits below the form; when a run starts, scroll it into view once.</summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PerformanceViewModel.IsActive) && _viewModel is { IsActive: true })
            Dispatcher.UIThread.Post(() => this.BringIntoView(), DispatcherPriority.Background);
    }
}
