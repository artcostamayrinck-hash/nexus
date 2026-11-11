using System.Diagnostics;
using Nexus.Contracts.Connections;
using Nexus.Core.Connections;
using Nexus.Core.Errors;
using Nexus.Security.Validation;

namespace Nexus.Desktop.Services.Connectivity;

public sealed record ServerProbeResult(bool IsReachable, int LatencyMs, string StatusLabel, string? Detail);

public interface IServerHealthProbeService
{
    Task<Result<ServerProbeResult>> ProbeAsync(string endpoint, CancellationToken cancellationToken = default);
}

public sealed class ServerHealthProbeService : IServerHealthProbeService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConnectionManager _connectionManager;

    public ServerHealthProbeService(
        IHttpClientFactory httpClientFactory,
        IConnectionManager connectionManager)
    {
        _httpClientFactory = httpClientFactory;
        _connectionManager = connectionManager;
    }

    public async Task<Result<ServerProbeResult>> ProbeAsync(string endpoint, CancellationToken cancellationToken = default)
    {
        var uriResult = InputSanitizer.ValidateEndpointUri(endpoint);
        if (uriResult.IsFailure)
        {
            return Result<ServerProbeResult>.Failure(uriResult.Error!);
        }

        var baseUri = uriResult.Value;
        var healthUri = new Uri(baseUri, "/health/live");

        await _connectionManager.TransitionAsync(
            ConnectionState.Discovering,
            TransportKind.LocalNetwork,
            baseUri.ToString().TrimEnd('/'),
            reason: "Verificando servidor local NEXUS...",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var sw = Stopwatch.StartNew();
        try
        {
            using var client = _httpClientFactory.CreateClient("NexusProbe");
            client.Timeout = TimeSpan.FromSeconds(3);

            using var response = await client.GetAsync(healthUri, cancellationToken).ConfigureAwait(false);
            sw.Stop();
            var latency = (int)Math.Max(1, sw.ElapsedMilliseconds);

            if (response.IsSuccessStatusCode)
            {
                await _connectionManager.TransitionAsync(
                    ConnectionState.Connected,
                    TransportKind.LocalNetwork,
                    baseUri.ToString().TrimEnd('/'),
                    latencyMs: latency,
                    reason: "Servidor local acessível",
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                return Result<ServerProbeResult>.Success(new ServerProbeResult(
                    true,
                    latency,
                    $"Online ({latency} ms)",
                    $"HTTP {(int)response.StatusCode}"));
            }

            await _connectionManager.TransitionAsync(
                ConnectionState.Degraded,
                TransportKind.LocalNetwork,
                baseUri.ToString().TrimEnd('/'),
                latencyMs: latency,
                reason: $"HTTP {(int)response.StatusCode}",
                cancellationToken: cancellationToken).ConfigureAwait(false);

            return Result<ServerProbeResult>.Success(new ServerProbeResult(
                false,
                latency,
                $"Degradado (HTTP {(int)response.StatusCode})",
                response.ReasonPhrase));
        }
        catch (OperationCanceledException)
        {
            return Result<ServerProbeResult>.Failure(NexusError.Cancelled());
        }
        catch (Exception ex)
        {
            sw.Stop();
            await _connectionManager.TransitionAsync(
                ConnectionState.Disconnected,
                TransportKind.Unknown,
                baseUri.ToString().TrimEnd('/'),
                reason: "Servidor local offline ou inacessível",
                cancellationToken: cancellationToken).ConfigureAwait(false);

            return Result<ServerProbeResult>.Success(new ServerProbeResult(
                false,
                0,
                "Servidor offline",
                ex.Message));
        }
    }
}

