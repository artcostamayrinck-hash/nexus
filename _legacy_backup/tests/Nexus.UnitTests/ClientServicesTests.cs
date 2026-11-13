using Microsoft.Extensions.DependencyInjection;
using Nexus.Contracts.Devices;
using Nexus.Contracts.Pairing;
using Nexus.Contracts.Security;
using Nexus.Core.Abstractions;
using Nexus.Core.Pairing;
using Nexus.Desktop.Services.DependencyInjection;
using Nexus.Desktop.Services.Settings;
using Nexus.Desktop.Services.ViewModels;
using Nexus.Infrastructure.DependencyInjection;
using Nexus.Infrastructure.Security;
using Nexus.Mobile.Services.DependencyInjection;
using Nexus.Mobile.Services.Settings;
using Nexus.Mobile.Services.ViewModels;
using Nexus.Modules.BuiltIn;
using Nexus.Security.Credentials;
using Nexus.Security.Identity;
using Nexus.Security.Tokens;
using Xunit;

namespace Nexus.UnitTests;

public sealed class ClientServicesTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "nexus-unit-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task DesktopServices_InitializeShell_AndPersistSettings()
    {
        var services = new ServiceCollection();
        services.AddNexusInfrastructure(
            configureStorage: s =>
            {
                s.RootDirectory = Path.Combine(_tempDir, "desktop");
                s.EnablePooling = false;
            },
            configureLogging: l => l.EnableConsole = false,
            configureLocalDevice: d =>
            {
                d.Platform = DevicePlatform.Windows;
                d.DeviceType = DeviceType.Desktop;
            });
        services.AddNexusModules();
        services.AddNexusDesktopServices();

        await using var provider = services.BuildServiceProvider();
        var shell = provider.GetRequiredService<MainShellViewModel>();
        await shell.InitializeAsync();

        Assert.True(shell.IsStorageReady);
        Assert.NotEmpty(shell.Devices.Devices);
        Assert.Matches("^[0-9A-F]{4}(-[0-9A-F]{4})+$", shell.Pairing.LocalFingerprintDisplay);

        var settingsService = provider.GetRequiredService<IDesktopSettingsService>();
        var invalidSave = await settingsService.SaveAsync(new DesktopSettingsModel { DeviceName = "X", ServerEndpoint = "ftp://bad" });
        Assert.True(invalidSave.IsFailure);

        var validSave = await settingsService.SaveAsync(new DesktopSettingsModel { DeviceName = "Estação Principal", ServerEndpoint = "http://127.0.0.1:5280" });
        Assert.True(validSave.IsSuccess);

        var loaded = await settingsService.LoadAsync();
        Assert.True(loaded.IsSuccess);
        Assert.Equal("Estação Principal", loaded.Value.DeviceName);
    }

    [Fact]
    public async Task MobileServices_InitializeHome_AndPersistSettings()
    {
        var services = new ServiceCollection();
        services.AddNexusInfrastructure(
            configureStorage: s =>
            {
                s.RootDirectory = Path.Combine(_tempDir, "mobile");
                s.EnablePooling = false;
            },
            configureLogging: l => l.EnableConsole = false,
            configureLocalDevice: d =>
            {
                d.DeviceName = "Galaxy S24";
                d.Platform = DevicePlatform.Android;
                d.DeviceType = DeviceType.Phone;
            });
        services.AddNexusModules();
        services.AddNexusMobileServices();

        await using var provider = services.BuildServiceProvider();
        var home = provider.GetRequiredService<HomeViewModel>();
        await home.InitializeAsync();

        Assert.Equal("Galaxy S24", home.DeviceTitle);
        Assert.Equal("SQLite Operacional", home.StorageBadge);
        Assert.Equal(1, home.KnownDevicesCount);
        Assert.NotEqual("-", home.LocalFingerprintDisplay);

        var mobileSettings = provider.GetRequiredService<IMobileSettingsService>();
        var saved = await mobileSettings.SaveAsync(new MobileSettingsModel
        {
            DeviceName = "Pixel 9 Pro",
            ServerEndpoint = "http://192.168.0.15:5280"
        });
        Assert.True(saved.IsSuccess);

        var settingsVm = provider.GetRequiredService<MobileSettingsViewModel>();
        await settingsVm.LoadAsync();
        Assert.Equal("Pixel 9 Pro", settingsVm.DeviceName);
    }

    [Fact]
    public async Task DesktopPairingViewModel_And_DevicesViewModel_CompleteLocalPairingRenameRevokeAndRemove()
    {
        var desktopServices = new ServiceCollection();
        desktopServices.AddNexusInfrastructure(
            configureStorage: s =>
            {
                s.RootDirectory = Path.Combine(_tempDir, "desktop-pair");
                s.EnablePooling = false;
            },
            configureLogging: l => l.EnableConsole = false,
            configureLocalDevice: d =>
            {
                d.DeviceName = "Windows Workstation";
                d.Platform = DevicePlatform.Windows;
                d.DeviceType = DeviceType.Desktop;
            });
        desktopServices.AddNexusModules();
        desktopServices.AddNexusDesktopServices();

        await using var desktopProvider = desktopServices.BuildServiceProvider();
        var shell = desktopProvider.GetRequiredService<MainShellViewModel>();
        await shell.InitializeAsync();

        // Step 1-2: Windows requests pairing & generates temporary QR
        await shell.Pairing.GenerateQrCommand.ExecuteAsync();
        Assert.Equal(PairingSessionState.AwaitingScan, shell.Pairing.CurrentState);
        Assert.NotNull(shell.Pairing.QrPngBytes);
        Assert.False(string.IsNullOrWhiteSpace(shell.Pairing.QrPayloadJson));

        // Step 3-4: Android identity parses QR and claims session
        var androidVault = new PlatformSecureCredentialStore(new SecureCredentialStoreOptions
        {
            VaultDirectory = Path.Combine(_tempDir, "android-vault")
        });
        var androidIdentity = new CryptographicIdentityProvider(
            androidVault,
            new DefaultLocalDeviceContext(Guid.Empty, "Pixel 9 Android", DevicePlatform.Android, DeviceType.Phone, "Android 15", "0.3.0", DeviceCapability.All),
            new SystemTextJsonSerializer(),
            new SystemClock());
        var androidProtocol = new NexusPairingProtocolClient(
            androidIdentity,
            new SecureTokenGenerator(),
            new SystemTextJsonSerializer(),
            new SystemClock());

        var parsedQr = androidProtocol.ParseAndVerifyQrPayload(shell.Pairing.QrPayloadJson);
        Assert.True(parsedQr.IsSuccess);

        var claim = await androidProtocol.BuildClaimRequestAsync(parsedQr.Value, NexusPermission.ViewStatus | NexusPermission.Notifications);
        Assert.True(claim.IsSuccess);

        using (var scope = desktopProvider.CreateScope())
        {
            var pairingSvc = scope.ServiceProvider.GetRequiredService<IPairingService>();
            var claimSnap = await pairingSvc.SubmitClaimAsync(claim.Value.Request);
            Assert.True(claimSnap.IsSuccess);
            Assert.Equal(PairingSessionState.AwaitingConfirmation, claimSnap.Value.State);
            Assert.Equal(claim.Value.VerificationCode, claimSnap.Value.VerificationCode);
        }

        // Poll on Windows ViewModel
        await shell.Pairing.PollStatusCommand.ExecuteAsync();
        Assert.Equal(PairingSessionState.AwaitingConfirmation, shell.Pairing.CurrentState);
        Assert.Equal("Pixel 9 Android", shell.Pairing.CandidateDeviceName);

        // Step 5-7: Approve pairing on Windows
        await shell.Pairing.ApprovePairingCommand.ExecuteAsync();
        Assert.Equal(PairingSessionState.Connected, shell.Pairing.CurrentState);

        // Trusted device management: inspect, rename, update permissions, revoke, remove
        var paired = Assert.Single(shell.Devices.TrustedDevices);
        Assert.Equal(DeviceTrustStatus.Authorized, paired.TrustStatus);
        Assert.NotEqual("-", shell.Devices.SelectedFingerprintDisplay);
        Assert.NotEqual("-", shell.Devices.SelectedLastAccessText);

        shell.Devices.EditTrustedName = "Pixel 9 Pessoal";
        await shell.Devices.RenameTrustedDeviceCommand.ExecuteAsync();
        Assert.Equal("Pixel 9 Pessoal", shell.Devices.SelectedTrustedDevice?.CustomName);

        shell.Devices.PermManageDevices = true;
        await shell.Devices.UpdateTrustedPermissionsCommand.ExecuteAsync();
        Assert.True(shell.Devices.SelectedTrustedDevice?.Permissions.HasFlag(NexusPermission.ManageDevices));

        shell.Devices.RevokeReason = "Teste de revogação UI";
        await shell.Devices.RevokeTrustedDeviceCommand.ExecuteAsync();
        Assert.Equal(DeviceTrustStatus.Revoked, shell.Devices.SelectedTrustedDevice?.TrustStatus);

        await shell.Devices.RemoveTrustedDeviceCommand.ExecuteAsync();
        Assert.Empty(shell.Devices.TrustedDevices);
    }

    [Fact]
    public async Task MobileConnectionViewModel_RejectsInvalidOrTamperedQrCode_WithClearState()
    {
        var services = new ServiceCollection();
        services.AddNexusInfrastructure(
            configureStorage: s =>
            {
                s.RootDirectory = Path.Combine(_tempDir, "mobile-invalid-qr");
                s.EnablePooling = false;
            },
            configureLogging: l => l.EnableConsole = false,
            configureLocalDevice: d =>
            {
                d.DeviceName = "Pixel Test";
                d.Platform = DevicePlatform.Android;
                d.DeviceType = DeviceType.Phone;
            });
        services.AddNexusModules();
        services.AddNexusMobileServices();

        await using var provider = services.BuildServiceProvider();
        var connVm = provider.GetRequiredService<ConnectionViewModel>();
        await connVm.LoadAsync();

        connVm.QrInputText = "{ invalid-json }";
        await connVm.SubmitQrPairingCommand.ExecuteAsync();

        Assert.Equal(PairingSessionState.Failed, connVm.PairingState);
        Assert.Equal("Falha", connVm.PairingStateLabel);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup lock in test teardown
        }
    }
}
