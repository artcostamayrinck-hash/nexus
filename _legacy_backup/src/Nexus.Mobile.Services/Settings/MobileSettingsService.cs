using Nexus.Core.Errors;
using Nexus.Core.Storage;
using Nexus.Security.Validation;

namespace Nexus.Mobile.Services.Settings;

public sealed record MobileSettingsModel
{
    public string DeviceName { get; init; } = "Android Device";
    public string ServerEndpoint { get; init; } = "http://192.168.1.100:5280";
    public bool AutoReconnect { get; init; } = true;
    public bool BackgroundTelemetryEnabled { get; init; } = true;
}

public interface IMobileSettingsService
{
    Task<Result<MobileSettingsModel>> LoadAsync(CancellationToken cancellationToken = default);
    Task<Result> SaveAsync(MobileSettingsModel settings, CancellationToken cancellationToken = default);
    Task<Result<MobileSettingsModel>> ResetAsync(CancellationToken cancellationToken = default);
}

public sealed class MobileSettingsService : IMobileSettingsService
{
    private const string Key = "mobile.preferences.v1";
    private readonly ISettingsStore _settingsStore;

    public MobileSettingsService(ISettingsStore settingsStore)
    {
        _settingsStore = settingsStore;
    }

    public Task<Result<MobileSettingsModel>> LoadAsync(CancellationToken cancellationToken = default)
        => _settingsStore.GetOrDefaultAsync(Key, new MobileSettingsModel(), cancellationToken);

    public Task<Result> SaveAsync(MobileSettingsModel settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (string.IsNullOrWhiteSpace(settings.DeviceName) || settings.DeviceName.Trim().Length < 2)
        {
            return Task.FromResult(Result.Failure(
                NexusError.Validation("Nome do dispositivo inválido.", code: "MOBILE_DEVICE_NAME_INVALID")));
        }

        var endpoint = InputSanitizer.ValidateEndpointUri(settings.ServerEndpoint);
        if (endpoint.IsFailure)
        {
            return Task.FromResult(Result.Failure(endpoint.Error!));
        }

        var sanitized = settings with
        {
            DeviceName = settings.DeviceName.Trim(),
            ServerEndpoint = endpoint.Value.ToString().TrimEnd('/')
        };

        return _settingsStore.SetAsync(Key, sanitized, cancellationToken);
    }

    public async Task<Result<MobileSettingsModel>> ResetAsync(CancellationToken cancellationToken = default)
    {
        var defaults = new MobileSettingsModel();
        var result = await SaveAsync(defaults, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Result<MobileSettingsModel>.Success(defaults) : Result<MobileSettingsModel>.Failure(result.Error!);
    }
}

