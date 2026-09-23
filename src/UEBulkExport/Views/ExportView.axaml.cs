using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;
using UEBulkExport.Gui.ViewModels;

namespace UEBulkExport.Gui.Views;

public sealed partial class ExportView : UserControl
{
    private ExportViewModel? _viewModel;

    public ExportView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_viewModel is not null) _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel = DataContext as ExportViewModel;
            if (_viewModel is not null) _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        };
    }

    /// <summary>The result lands below the fold on a long form, so bring it into view when it appears.</summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_viewModel is null) return;

        switch (e.PropertyName)
        {
            case nameof(ExportViewModel.HasSummary) when _viewModel.HasSummary:
                Dispatcher.UIThread.Post(() => SummaryCard.BringIntoView(), DispatcherPriority.Background);
                break;
            case nameof(ExportViewModel.HasError) when _viewModel.HasError:
                Dispatcher.UIThread.Post(() => ErrorCard.BringIntoView(), DispatcherPriority.Background);
                break;
        }
    }
}
