using Nexus.Mobile.Services.ViewModels;

namespace Nexus.Mobile.Views;

public partial class HomePage : ContentPage
{
    private readonly HomeViewModel _viewModel;

    public HomePage(HomeViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
        _viewModel.NavigateToModulesRequested += async (_, _) =>
        {
            if (Shell.Current is not null)
            {
                await Shell.Current.GoToAsync("//modules");
            }
        };
        _viewModel.NavigateToPanelRequested += async (_, _) =>
        {
            if (Shell.Current is not null)
            {
                await Shell.Current.GoToAsync("//panel");
            }
        };
        _viewModel.NavigateToAutomateRequested += async (_, _) =>
        {
            if (Shell.Current is not null)
            {
                await Shell.Current.GoToAsync("//automate");
            }
        };
        _viewModel.NavigateToUpdatesRequested += async (_, _) =>
        {
            if (Shell.Current is not null)
            {
                await Shell.Current.GoToAsync("//updates");
            }
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _viewModel.InitializeAsync();
    }
}

