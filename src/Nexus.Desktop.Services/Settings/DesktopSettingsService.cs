using Nexus.Core.Errors;
using Nexus.Core.Storage;
using Nexus.Security.Validation;

namespace Nexus.Desktop.Services.Settings;

public sealed record DesktopSettingsModel
{
    public string DeviceName { get; init; } = Environment.MachineName;
    public string ServerEndpoint { get; init; } = "http://127.0.0.1:5280";
    public bool AutoProbeServer { get; init; } = true;
    public bool EnableSubtleAnimations { get; init; } = true;
    public string LogLevel { get; init; } = "Information";
}

public interface IDesktopSettingsService
{
    Task<Result<DesktopSettingsModel>> LoadAsync(CancellationToken cancellationToken = default);
    Task<Result> SaveAsync(DesktopSettingsModel settings, CancellationToken cancellationToken = default);
    Task<Result<DesktopSettingsModel>> ResetToDefaultsAsync(CancellationToken cancellationToken = default);
}

public sealed class DesktopSettingsService : IDesktopSettingsService
{
    private const string SettingsKey = "desktop.preferences.v1";
    private readonly ISettingsStore _settingsStore;

    public DesktopSettingsService(ISettingsStore settingsStore)
    {
        _settingsStore = settingsStore;
    }

    public Task<Result<DesktopSettingsModel>> LoadAsync(CancellationToken cancellationToken = default)
        => _settingsStore.GetOrDefaultAsync(SettingsKey, new DesktopSettingsModel(), cancellationToken);

    public Task<Result> SaveAsync(DesktopSettingsModel settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (string.IsNullOrWhiteSpace(settings.DeviceName) || settings.DeviceName.Trim().Length < 2)
        {
            return Task.FromResult(Result.Failure(
                NexusError.Validation("O nome do dispositivo deve ter pelo menos 2 caracteres.", code: "DEVICE_NAME_INVALID")));
        }

        var endpointCheck = InputSanitizer.ValidateEndpointUri(settings.ServerEndpoint);
        if (endpointCheck.IsFailure)
        {
            return Task.FromResult(Result.Failure(endpointCheck.Error!));
        }

        var sanitized = settings with
        {
            DeviceName = settings.DeviceName.Trim(),
            ServerEndpoint = endpointCheck.Value.ToString().TrimEnd('/')
        };

        return _settingsStore.SetAsync(SettingsKey, sanitized, cancellationToken);
    }

    public async Task<Result<DesktopSettingsModel>> ResetToDefaultsAsync(CancellationToken cancellationToken = default)
    {
        var defaults = new DesktopSettingsModel();
        var saved = await SaveAsync(defaults, cancellationToken).ConfigureAwait(false);
        return saved.IsSuccess
            ? Result<DesktopSettingsModel>.Success(defaults)
            : Result<DesktopSettingsModel>.Failure(saved.Error!);
    }
}

