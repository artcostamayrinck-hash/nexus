using Nexus.Mobile.Services.ViewModels;

namespace Nexus.Mobile.Views;

public partial class TransferPage : ContentPage
{
    private readonly MobileTransferViewModel _viewModel;

    public TransferPage(MobileTransferViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _viewModel.InitializeAsync();
    }

    private async void OnPickFilesClicked(object? sender, EventArgs e)
    {
        try
        {
            var picks = await FilePicker.Default.PickMultipleAsync(new PickOptions
            {
                PickerTitle = "Selecionar arquivos para enviar"
            });

            if (picks is null)
            {
                return;
            }

            var paths = picks
                .Select(p => p.FullPath)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .ToArray();

            if (paths.Length > 0)
            {
                _viewModel.AddSelectedFiles(paths);
            }
        }
        catch (Exception)
        {
            // Se o usuário cancelar ou o seletor não estiver disponível no ambiente atual, preserva a fila atual.
        }
    }
}

