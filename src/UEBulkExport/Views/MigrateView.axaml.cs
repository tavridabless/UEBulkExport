using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;
using UEBulkExport.Gui.ViewModels;

namespace UEBulkExport.Gui.Views;

public sealed partial class MigrateView : UserControl
{
    private MigrateViewModel? _viewModel;

    public MigrateView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_viewModel is not null) _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel = DataContext as MigrateViewModel;
            if (_viewModel is not null) _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        };
    }

    /// <summary>The outcome lands below the form, so bring it into view when it appears.</summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_viewModel is null) return;

        switch (e.PropertyName)
        {
            case nameof(MigrateViewModel.HasSummary) when _viewModel.HasSummary:
                Dispatcher.UIThread.Post(() => SummaryCard.BringIntoView(), DispatcherPriority.Background);
                break;
            case nameof(MigrateViewModel.HasError) when _viewModel.HasError:
                Dispatcher.UIThread.Post(() => ErrorCard.BringIntoView(), DispatcherPriority.Background);
                break;
        }
    }
}
