using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexus.Desktop.Services.Connectivity;
using Nexus.Desktop.Services.Mvvm;
using Nexus.Desktop.Services.Security;
using Nexus.Desktop.Services.Settings;
using Nexus.Desktop.Services.ViewModels;

namespace Nexus.Desktop.Services.DependencyInjection;

public static class DesktopServicesExtensions
{
    public static IServiceCollection AddNexusDesktopServices(this IServiceCollection services)
    {
        services.AddHttpClient("NexusProbe");
        services.TryAddSingleton<IUiDispatcher>(ImmediateUiDispatcher.Instance);
        services.TryAddSingleton<IDesktopSettingsService, DesktopSettingsService>();
        services.TryAddSingleton<IServerHealthProbeService, ServerHealthProbeService>();
        services.TryAddSingleton<IDesktopPairingClient, DesktopPairingClient>();

        services.TryAddSingleton<DashboardViewModel>();
        services.TryAddSingleton<ConnectivityViewModel>();
        services.TryAddSingleton<RealtimeViewModel>();
        services.TryAddSingleton<SyncViewModel>();
        services.TryAddSingleton<TransferViewModel>();
        services.TryAddSingleton<ModulesViewModel>();
        services.TryAddSingleton<AutomationViewModel>();
        services.TryAddSingleton<PairingViewModel>();
        services.TryAddSingleton<DevicesViewModel>();
        services.TryAddSingleton<SettingsViewModel>();
        services.TryAddSingleton<SystemStatusViewModel>();
        services.TryAddSingleton<MainShellViewModel>();

        return services;
    }
}

