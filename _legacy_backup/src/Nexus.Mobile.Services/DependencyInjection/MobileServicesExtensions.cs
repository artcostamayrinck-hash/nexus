using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexus.Mobile.Services.Connectivity;
using Nexus.Mobile.Services.Security;
using Nexus.Mobile.Services.Settings;
using Nexus.Mobile.Services.ViewModels;

namespace Nexus.Mobile.Services.DependencyInjection;

public static class MobileServicesExtensions
{
    public static IServiceCollection AddNexusMobileServices(this IServiceCollection services)
    {
        services.AddHttpClient("NexusMobileProbe");
        services.TryAddSingleton<IMobileSettingsService, MobileSettingsService>();
        services.TryAddSingleton<IMobileConnectionCoordinator, MobileConnectionCoordinator>();
        services.TryAddSingleton<IMobilePairingClient, MobilePairingClient>();
        services.AddSingleton<Nexus.Core.Connections.IRealtimeCredentialProvider, MobileRealtimeCredentialProvider>();

        services.TryAddSingleton<HomeViewModel>();
        services.TryAddSingleton<ConnectionViewModel>();
        services.TryAddSingleton<MobileConnectivityViewModel>();
        services.TryAddSingleton<MobileRealtimeViewModel>();
        services.TryAddSingleton<MobileSyncViewModel>();
        services.TryAddSingleton<MobileTransferViewModel>();
        services.TryAddSingleton<MobileModulesViewModel>();
        services.TryAddSingleton<MobilePanelViewModel>();
        services.TryAddSingleton<MobileAutomateViewModel>();
        services.TryAddSingleton<MobileUpdateViewModel>();
        services.TryAddSingleton<MobileDevicesViewModel>();
        services.TryAddSingleton<MobileSettingsViewModel>();

        return services;
    }
}

