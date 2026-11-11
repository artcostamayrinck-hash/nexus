using Nexus.Mobile.Services.ViewModels;

namespace Nexus.Mobile.Views;

public partial class ModulesPage : ContentPage
{
    private readonly MobileModulesViewModel _vm;

    public ModulesPage(MobileModulesViewModel vm)
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

