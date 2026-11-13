using Nexus.Mobile.Services.ViewModels;

namespace Nexus.Mobile.Views;

public partial class SyncPage : ContentPage
{
    private readonly MobileSyncViewModel _viewModel;

    public SyncPage(MobileSyncViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _viewModel.InitializeAsync();
    }
}

