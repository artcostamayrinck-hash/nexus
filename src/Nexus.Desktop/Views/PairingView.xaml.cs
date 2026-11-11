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
        if (DataContext is PairingViewModel vm && !string.IsNullOrWhiteSpace(vm.QrPayloadJson))
        {
            Clipboard.SetText(vm.QrPayloadJson);
        }
    }
}

