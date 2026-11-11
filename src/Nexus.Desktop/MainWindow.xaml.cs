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

        var fadeIn = new DoubleAnimation(0.35, 1.0, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        ContentHost.BeginAnimation(OpacityProperty, fadeIn);
    }
}

