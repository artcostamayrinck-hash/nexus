using Nexus.Mobile.Services.ViewModels;

namespace Nexus.Mobile.Views;

public partial class DevicesPage : ContentPage
{
    private readonly MobileDevicesViewModel _viewModel;

    public DevicesPage(MobileDevicesViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _viewModel.LoadAsync();
    }
}

