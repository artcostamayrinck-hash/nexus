using Nexus.Mobile.Services.ViewModels;

namespace Nexus.Mobile.Views;

public partial class RoutesPage : ContentPage
{
    private readonly MobileConnectivityViewModel _viewModel;

    public RoutesPage(MobileConnectivityViewModel viewModel)
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

