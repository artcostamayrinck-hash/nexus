using Nexus.Mobile.Services.ViewModels;

namespace Nexus.Mobile.Views;

public partial class SettingsPage : ContentPage
{
    private readonly MobileSettingsViewModel _viewModel;

    public SettingsPage(MobileSettingsViewModel viewModel)
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

