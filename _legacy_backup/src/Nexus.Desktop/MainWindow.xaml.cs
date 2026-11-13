using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace Nexus.Desktop;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void OnNavigationSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ContentHost is null)
        {
            return;
        }

        if (DataContext is Nexus.Desktop.Services.ViewModels.MainShellViewModel vm &&
            !vm.Settings.EnableSubtleAnimations)
        {
            ContentHost.BeginAnimation(OpacityProperty, null);
            ContentHost.Opacity = 1.0;
            return;
        }

        var fadeIn = new DoubleAnimation(0.35, 1.0, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        ContentHost.BeginAnimation(OpacityProperty, fadeIn);
    }
}

