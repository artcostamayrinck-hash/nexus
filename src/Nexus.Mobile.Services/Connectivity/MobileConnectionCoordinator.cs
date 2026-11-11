using System.Diagnostics;
using Nexus.Contracts.Connections;
using Nexus.Core.Connections;
using Nexus.Core.Errors;
using Nexus.Security.Validation;

namespace Nexus.Mobile.Services.Connectivity;

public interface IMobileConnectionCoordinator
{
    ConnectionStateSnapshot CurrentSnapshot { get; }
    Task<Result<ConnectionStateSnapshot>> TestConnectionAsync(string endpoint, TransportKind transport, CancellationToken cancellationToken = default);
    Task<Result<ConnectionStateSnapshot>> DisconnectAsync(CancellationToken cancellationToken = default);
}

public sealed class MobileConnectionCoordinator : IMobileConnectionCoordinator
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConnectionManager _connectionManager;

    public MobileConnectionCoordinator(
        IHttpClientFactory httpClientFactory,
        IConnectionManager connectionManager)
    {
        _httpClientFactory = httpClientFactory;
        _connectionManager = connectionManager;
    }

    public ConnectionStateSnapshot CurrentSnapshot => _connectionManager.CurrentSnapshot;

    public async Task<Result<ConnectionStateSnapshot>> TestConnectionAsync(
        string endpoint,
        TransportKind transport,
        CancellationToken cancellationToken = default)
    {
        var uriValidation = InputSanitizer.ValidateEndpointUri(endpoint);
        if (uriValidation.IsFailure)
        {
            return Result<ConnectionStateSnapshot>.Failure(uriValidation.Error!);
        }

        var baseUri = uriValidation.Value;
        await _connectionManager.TransitionAsync(
            ConnectionState.Connecting,
            transport,
            baseUri.ToString().TrimEnd('/'),
            reason: "Testando alcance do servidor NEXUS...",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var sw = Stopwatch.StartNew();
        try
        {
            using var client = _httpClientFactory.CreateClient("NexusMobileProbe");
            client.Timeout = TimeSpan.FromSeconds(4);

            using var response = await client.GetAsync(new Uri(baseUri, "/health/live"), cancellationToken).ConfigureAwait(false);
            sw.Stop();
            var latency = (int)Math.Max(1, sw.ElapsedMilliseconds);

            if (response.IsSuccessStatusCode)
            {
                return await _connectionManager.TransitionAsync(
                    ConnectionState.Connected,
                    transport,
                    baseUri.ToString().TrimEnd('/'),
                    latencyMs: latency,
                    reason: "Servidor alcançável",
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            return await _connectionManager.TransitionAsync(
                ConnectionState.Degraded,
                transport,
                baseUri.ToString().TrimEnd('/'),
                latencyMs: latency,
                reason: $"HTTP {(int)response.StatusCode}",
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Result<ConnectionStateSnapshot>.Failure(NexusError.Cancelled());
        }
        catch (Exception ex)
        {
            return await _connectionManager.TransitionAsync(
                ConnectionState.Faulted,
                transport,
                baseUri.ToString().TrimEnd('/'),
                reason: ex.Message,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    public Task<Result<ConnectionStateSnapshot>> DisconnectAsync(CancellationToken cancellationToken = default)
        => _connectionManager.DisconnectAsync("Desconectado pelo aplicativo Android", cancellationToken);
}

