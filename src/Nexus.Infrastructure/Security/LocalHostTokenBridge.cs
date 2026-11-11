using Nexus.Core.Errors;
using Nexus.Security.Credentials;

namespace Nexus.Infrastructure.Security;

public sealed record HostBridgeTokenEnvelope(
    Guid ServerDeviceId,
    string AccessToken,
    DateTimeOffset ExpiresAtUtc,
    string? TlsThumbprint);

public sealed class LocalHostTokenBridgeOptions
{
    public bool Enabled { get; set; } = true;
    public string DirectoryPath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NEXUS",
        "host-bridge");
}

public interface ILocalHostTokenBridge
{
    Task<Result> WriteAsync(HostBridgeTokenEnvelope envelope, CancellationToken cancellationToken = default);
    Task<Result<HostBridgeTokenEnvelope?>> ReadAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// DPAPI / AES-GCM protected credential bridge between a local Windows NEXUS Server and the Windows NEXUS Desktop host UI.
/// Never exposed over HTTP; requires same OS user context and valid Bearer token verification on the server.
/// </summary>
public sealed class LocalHostTokenBridge : ILocalHostTokenBridge
{
    private const string SecretKey = "local.host.bridge.v1";
    private readonly LocalHostTokenBridgeOptions _options;
    private readonly ISecureCredentialStore _credentialStore;
    private readonly Core.Abstractions.IJsonSerializer _serializer;

    public LocalHostTokenBridge(
        LocalHostTokenBridgeOptions options,
        Core.Abstractions.IJsonSerializer serializer)
    {
        _options = options;
        _serializer = serializer;
        _credentialStore = new PlatformSecureCredentialStore(new SecureCredentialStoreOptions
        {
            VaultDirectory = options.DirectoryPath
        });
    }

    public async Task<Result> WriteAsync(HostBridgeTokenEnvelope envelope, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return Result.Success();
        }

        ArgumentNullException.ThrowIfNull(envelope);
        var json = _serializer.Serialize(envelope);
        return await _credentialStore.SaveStringAsync(SecretKey, json, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<HostBridgeTokenEnvelope?>> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return Result<HostBridgeTokenEnvelope?>.Success(null);
        }

        var loaded = await _credentialStore.GetStringAsync(SecretKey, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return Result<HostBridgeTokenEnvelope?>.Failure(loaded.Error!);
        }

        if (string.IsNullOrWhiteSpace(loaded.Value))
        {
            return Result<HostBridgeTokenEnvelope?>.Success(null);
        }

        var envelope = _serializer.Deserialize<HostBridgeTokenEnvelope>(loaded.Value);
        if (envelope is null || envelope.ExpiresAtUtc <= DateTimeOffset.UtcNow.AddMinutes(1))
        {
            return Result<HostBridgeTokenEnvelope?>.Success(null);
        }

        return Result<HostBridgeTokenEnvelope?>.Success(envelope);
    }
}
