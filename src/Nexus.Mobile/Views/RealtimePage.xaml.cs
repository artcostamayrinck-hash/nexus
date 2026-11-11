using Nexus.Mobile.Services.ViewModels;

namespace Nexus.Mobile.Views;

public partial class RealtimePage : ContentPage
{
    private readonly MobileRealtimeViewModel _viewModel;

    public RealtimePage(MobileRealtimeViewModel viewModel)
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

