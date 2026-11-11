using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Nexus.Contracts.Devices;
using Nexus.Core.Sync;
using Nexus.Desktop.Services;
using Nexus.Desktop.Services.DependencyInjection;
using Nexus.Desktop.Services.ViewModels;
using Nexus.Infrastructure.DependencyInjection;
using Nexus.Infrastructure.Security;
using Nexus.Modules.BuiltIn;
using Nexus.Security.Identity;

namespace Nexus.Desktop;

public partial class App : Application
{
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddNexusInfrastructure(
                    configureStorage: storage =>
                    {
                        storage.RootDirectory = System.IO.Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                            "NEXUS",
                            "desktop");
                        storage.DatabaseFileName = "nexus-desktop.db";
                    },
                    configureLogging: logging =>
                    {
                        logging.ApplicationName = "NEXUS.Desktop";
                    },
                    configureLocalDevice: device =>
                    {
                        device.Platform = DevicePlatform.Windows;
                        device.DeviceType = DeviceType.Desktop;
                    });

                services.AddNexusModules();
                services.RemoveAll<ISystemClipboardAdapter>();
                services.AddSingleton<ISystemClipboardAdapter, WindowsClipboardAdapter>();
                services.AddNexusDesktopServices();
                services.AddSingleton(new LocalHostTokenBridgeOptions { Enabled = OperatingSystem.IsWindows() });
                services.AddSingleton<MainWindow>();
            })
            .Build();

        await _host.StartAsync();

        var cryptoIdentity = _host.Services.GetRequiredService<ICryptographicIdentityProvider>();
        await cryptoIdentity.GetOrCreateIdentityAsync();

        var shellVm = _host.Services.GetRequiredService<MainShellViewModel>();
        await shellVm.InitializeAsync();

        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        mainWindow.DataContext = shellVm;
        mainWindow.Show();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(3));
            _host.Dispose();
        }

        base.OnExit(e);
    }
}

