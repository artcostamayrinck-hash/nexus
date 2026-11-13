using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Nexus.Desktop.Services.ViewModels;

namespace Nexus.Desktop.Views;

public partial class TransferView : UserControl
{
    public TransferView()
    {
        InitializeComponent();
    }

    private void OnBrowseFilesClicked(object sender, RoutedEventArgs e)
    {
        if (DataContext is not TransferViewModel vm)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Selecionar arquivos para enviar via NEXUS",
            Multiselect = true,
            CheckFileExists = true,
            Filter = "Todos os arquivos (*.*)|*.*"
        };

        if (dialog.ShowDialog() == true && dialog.FileNames.Length > 0)
        {
            vm.AddSelectedFiles(dialog.FileNames);
        }
    }
}

