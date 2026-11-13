using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Nexus.Contracts.Devices;
using Nexus.Core.Sync;
using Nexus.Desktop.Services;
using Nexus.Desktop.Services.DependencyInjection;
using Nexus.Desktop.Services.Mvvm;
using Nexus.Desktop.Services.ViewModels;
using Nexus.Infrastructure.DependencyInjection;
using Nexus.Infrastructure.Security;
using Nexus.Modules.BuiltIn;
using Nexus.Security.Identity;

namespace Nexus.Desktop;

public partial class App : Application
{
    private IHost? _host;
    private CancellationTokenSource? _shutdownCts;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _shutdownCts = new CancellationTokenSource();
        var mainUiDispatcher = Dispatcher;

        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            MessageBox.Show(
                $"Ocorreu um erro inesperado na interface NEXUS:\n{args.Exception.Message}",
                "NEXUS — Aviso de Estabilidade",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        };

        try
        {
            var startupToken = _shutdownCts.Token;

            _host = Host.CreateDefaultBuilder()
                .ConfigureServices(services =>
                {
                    services.AddSingleton<IUiDispatcher>(new WpfUiDispatcher(mainUiDispatcher));
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

            await _host.StartAsync(startupToken);

            var cryptoIdentity = _host.Services.GetRequiredService<ICryptographicIdentityProvider>();
            await cryptoIdentity.GetOrCreateIdentityAsync(startupToken);

            var shellVm = _host.Services.GetRequiredService<MainShellViewModel>();
            var mainWindow = _host.Services.GetRequiredService<MainWindow>();
            mainWindow.DataContext = shellVm;
            mainWindow.Show();

            await shellVm.InitializeAsync(startupToken);
        }
        catch (OperationCanceledException)
        {
            // Encerramento solicitado pelo usuário durante inicialização.
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Não foi possível iniciar o NEXUS Desktop:\n{ex.Message}",
                "NEXUS — Erro de Inicialização",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_shutdownCts is not null)
        {
            await _shutdownCts.CancelAsync();
            _shutdownCts.Dispose();
            _shutdownCts = null;
        }

        if (_host is not null)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(3));
            _host.Dispose();
        }

        base.OnExit(e);
    }
}

