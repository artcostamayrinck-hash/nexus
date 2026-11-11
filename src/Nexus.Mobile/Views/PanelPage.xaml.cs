using Nexus.Mobile.Services.ViewModels;

namespace Nexus.Mobile.Views;

public partial class PanelPage : ContentPage
{
    private readonly MobilePanelViewModel _vm;

    public PanelPage(MobilePanelViewModel vm)
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

