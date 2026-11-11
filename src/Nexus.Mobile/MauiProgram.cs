using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexus.Contracts.Devices;
using Nexus.Core.Automation;
using Nexus.Core.Sync;
using Nexus.Core.Updates;
using Nexus.Infrastructure.DependencyInjection;
using Nexus.Mobile.Services;
using Nexus.Mobile.Services.DependencyInjection;
using Nexus.Mobile.Views;
using Nexus.Modules.BuiltIn;
using Nexus.Security.Updates;

namespace Nexus.Mobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

        builder.Services.AddNexusInfrastructure(
            configureStorage: storage =>
            {
                storage.RootDirectory = Path.Combine(FileSystem.AppDataDirectory, "nexus");
                storage.DatabaseFileName = "nexus-mobile.db";
            },
            configureLogging: logging =>
            {
                logging.ApplicationName = "NEXUS.Mobile";
                logging.EnableConsole = false;
            },
            configureLocalDevice: device =>
            {
                device.DeviceName = DeviceInfo.Name;
                device.Platform = DevicePlatform.Android;
                device.DeviceType = DeviceInfo.Idiom == DeviceIdiom.Tablet ? DeviceType.Tablet : DeviceType.Phone;
                device.OperatingSystemVersion = $"Android {DeviceInfo.VersionString}";
            });

        builder.Services.AddNexusModules();
        builder.Services.RemoveAll<ISystemClipboardAdapter>();
        builder.Services.AddSingleton<ISystemClipboardAdapter, AndroidClipboardAdapter>();
        builder.Services.RemoveAll<IDeviceBatteryProvider>();
        builder.Services.AddSingleton<IDeviceBatteryProvider, AndroidBatteryProvider>();
        builder.Services.RemoveAll<IApkArchiveReader>();
        builder.Services.AddSingleton<IApkArchiveReader, AndroidApkArchiveReader>();
        builder.Services.RemoveAll<IAppPackageInspector>();
        builder.Services.AddSingleton<IAppPackageInspector, AndroidAppPackageInspector>();
        builder.Services.RemoveAll<IAndroidUpdateInstaller>();
        builder.Services.AddSingleton<IAndroidUpdateInstaller, AndroidUpdateInstaller>();
        builder.Services.AddNexusMobileServices();

        builder.Services.AddSingleton<AppShell>();
        builder.Services.AddTransient<HomePage>();
        builder.Services.AddTransient<ConnectionPage>();
        builder.Services.AddTransient<RoutesPage>();
        builder.Services.AddTransient<RealtimePage>();
        builder.Services.AddTransient<SyncPage>();
        builder.Services.AddTransient<TransferPage>();
        builder.Services.AddTransient<ModulesPage>();
        builder.Services.AddTransient<PanelPage>();
        builder.Services.AddTransient<AutomatePage>();
        builder.Services.AddTransient<UpdatePage>();
        builder.Services.AddTransient<DevicesPage>();
        builder.Services.AddTransient<SettingsPage>();

        return builder.Build();
    }
}

