using Nexus.Mobile.Services.ViewModels;

namespace Nexus.Mobile.Views;

public partial class ConnectionPage : ContentPage
{
    private readonly ConnectionViewModel _viewModel;

    public ConnectionPage(ConnectionViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _viewModel.LoadAsync();
    }

    private async void OnPasteQrClick(object? sender, EventArgs e)
    {
        if (Clipboard.Default.HasText)
        {
            var text = await Clipboard.Default.GetTextAsync();
            if (!string.IsNullOrWhiteSpace(text))
            {
                _viewModel.QrInputText = text.Trim();
                _viewModel.SetPairingFeedback("Payload do QR Code colado da área de transferência. Toque em \"Parear Agora\".");
                return;
            }
        }

        _viewModel.SetPairingFeedback("Área de transferência vazia. Copie o JSON do QR Code exibido no NEXUS Windows.");
    }
}
