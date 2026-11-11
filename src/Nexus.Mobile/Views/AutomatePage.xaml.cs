using Nexus.Mobile.Services.ViewModels;

namespace Nexus.Mobile.Views;

public partial class AutomatePage : ContentPage
{
    private readonly MobileAutomateViewModel _vm;

    public AutomatePage(MobileAutomateViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _vm.InitializeAsync();
    }
}

