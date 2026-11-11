using System.Windows;
using System.Windows.Controls;
using Nexus.Desktop.Services.ViewModels;

namespace Nexus.Desktop.Views;

public partial class PairingView : UserControl
{
    public PairingView()
    {
        InitializeComponent();
    }

    private void OnCopyQrPayloadClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not PairingViewModel vm)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(vm.QrPayloadJson))
        {
            vm.ReportUiFeedback("Gere um QR Code primeiro antes de copiar o payload JSON.");
            return;
        }

        try
        {
            Clipboard.SetText(vm.QrPayloadJson);
            vm.ReportUiFeedback("Payload JSON do QR Code copiado para a área de transferência.");
        }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            vm.ReportUiFeedback($"Área de transferência bloqueada por outro aplicativo: {ex.Message}");
        }
    }
}

