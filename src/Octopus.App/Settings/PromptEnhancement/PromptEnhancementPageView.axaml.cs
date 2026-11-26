using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Octopus.App.Settings.PromptEnhancement;

public partial class PromptEnhancementPageView : UserControl
{
    private PromptEnhancementSettingsViewModel? _viewModel;
    private bool _reassertPosted;

    public PromptEnhancementPageView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Hook(DataContext as PromptEnhancementSettingsViewModel);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        Hook(DataContext as PromptEnhancementSettingsViewModel);
    }

    private void Hook(PromptEnhancementSettingsViewModel? viewModel)
    {
        if (ReferenceEquals(_viewModel, viewModel)) return;
        if (_viewModel is not null) _viewModel.DefaultChoices.CollectionChanged -= OnChoicesChanged;
        _viewModel = viewModel;
        if (_viewModel is not null) _viewModel.DefaultChoices.CollectionChanged += OnChoicesChanged;
    }

    private void OnChoicesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Arc Select keeps SelectedValue but drops the resolved row when its options are added, removed or
        // replaced, which blanks the field. Re-pick the saved id once the list has finished changing.
        if (_viewModel is null || _reassertPosted) return;
        _reassertPosted = true;
        Dispatcher.UIThread.Post(() =>
        {
            _reassertPosted = false;
            if (_viewModel is null) return;
            var id = _viewModel.DefaultEndpointId;
            DefaultEndpointSelect.SelectedValue = null;
            DefaultEndpointSelect.SelectedValue = id;
        });
    }
}
