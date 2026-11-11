using Nexus.Mobile.Services.ViewModels;

namespace Nexus.Mobile.Views;

public partial class UpdatePage : ContentPage
{
    private readonly MobileUpdateViewModel _viewModel;

    public UpdatePage(MobileUpdateViewModel viewModel)
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

