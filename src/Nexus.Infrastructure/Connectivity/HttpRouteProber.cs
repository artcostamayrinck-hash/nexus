using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using Nexus.Contracts.Common;
using Nexus.Contracts.Connections;
using Nexus.Contracts.Messaging;
using Nexus.Core.Abstractions;
using Nexus.Core.Connections;
using Nexus.Infrastructure.Security;

namespace Nexus.Infrastructure.Connectivity;

public sealed class ConnectivityOptions
{
    public TimeSpan ProbeTimeout { get; set; } = TimeSpan.FromSeconds(3);
    public TimeSpan MonitorInterval { get; set; } = TimeSpan.FromSeconds(10);
    public bool EnableBackgroundMonitor { get; set; } = true;
    public TimeSpan MdnsDiscoveryTimeout { get; set; } = TimeSpan.FromMilliseconds(1200);
}

public sealed class HttpRouteProber : IRouteProber
{
    private sealed record ProbeHostDeviceDto(
        Guid Id,
        string? Name);

    private sealed record ProbeSystemInfoDto(
        Guid DeviceId,
        string? DeviceName,
        ProbeHostDeviceDto? HostDevice,
        string? TlsThumbprint,
        string? HostKeyFingerprint);

    private readonly NexusPairingProtocolClient _protocolClient;
    private readonly IJsonSerializer _serializer;
    private readonly IClock _clock;
    private readonly ConnectivityOptions _options;

    public HttpRouteProber(
        NexusPairingProtocolClient protocolClient,
        IJsonSerializer serializer,
        IClock clock,
        ConnectivityOptions? options = null)
    {
        _protocolClient = protocolClient;
        _serializer = serializer;
        _clock = clock;
        _options = options ?? new ConnectivityOptions();
    }

    public async Task<RouteProbeResult> ProbeAsync(
        DeviceEndpointRecord endpoint,
        NetworkEnvironmentSnapshot networkEnvironment,
        CancellationToken cancellationToken = default)
    {
        var probedAt = _clock.UtcNow;
        if (!DeviceEndpointRecord.IsValidHttpOrWsUri(endpoint.EndpointUrl))
        {
            return Fail(endpoint, probedAt, RouteFailureReason.InvalidEndpoint, "Endpoint inválido: URL malformada ou esquema não suportado.");
        }

        var uri = new Uri(endpoint.EndpointUrl, UriKind.Absolute);
        var isLoopback = IsLoopbackHost(uri.Host);

        if (!networkEnvironment.IsNetworkAvailable && !isLoopback)
        {
            return Fail(endpoint, probedAt, RouteFailureReason.InternetUnavailable, "Sem conexão de rede ou internet disponível.");
        }

        if (endpoint.RouteKind == ConnectionRouteKind.LocalLan
            && !networkEnvironment.HasLocalLanInterface
            && networkEnvironment.IsCellularActive
            && !isLoopback)
        {
            return Fail(endpoint, probedAt, RouteFailureReason.LocalNetworkUnavailable, "Dispositivo em dados móveis; rede local LAN indisponível.");
        }

        if (endpoint.RouteKind == ConnectionRouteKind.RemoteVpnTailscale
            && networkEnvironment.VpnStatus == VpnStatus.Disconnected
            && !isLoopback)
        {
            return Fail(endpoint, probedAt, RouteFailureReason.VpnUnavailable, "VPN Tailscale desconectada neste dispositivo.");
        }

        var sw = Stopwatch.StartNew();
        try
        {
            using var client = _protocolClient.CreateHttpClient(
                endpoint.EndpointUrl,
                endpoint.ExpectedTlsThumbprint,
                _options.ProbeTimeout);

            using var response = await client.GetAsync("api/v1/system/info", cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return Fail(endpoint, probedAt, RouteFailureReason.Unauthorized, "Credencial não autorizada ou expirada no servidor.");
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                return Fail(endpoint, probedAt, RouteFailureReason.DeviceRevoked, "Acesso revogado para este dispositivo.");
            }

            if (response.IsSuccessStatusCode)
            {
                sw.Stop();
                var envelope = TryDeserializeInfo(body);
                if (envelope is { IsSuccess: true, Payload: not null })
                {
                    var info = envelope.Payload;
                    var observedId = info.HostDevice is { Id: var hid } && hid != Guid.Empty ? hid : info.DeviceId;
                    var observedName = !string.IsNullOrWhiteSpace(info.HostDevice?.Name) ? info.HostDevice.Name : info.DeviceName;

                    if (!string.IsNullOrWhiteSpace(endpoint.ExpectedHostKeyFingerprint)
                        && !string.Equals(endpoint.ExpectedHostKeyFingerprint, info.HostKeyFingerprint, StringComparison.OrdinalIgnoreCase))
                    {
                        return Fail(endpoint, probedAt, RouteFailureReason.IdentityMismatch, "Impressão criptográfica do servidor não confere com o dispositivo pareado.");
                    }

                    if (endpoint.DeviceId != Guid.Empty && observedId != Guid.Empty && endpoint.DeviceId != observedId)
                    {
                        return Fail(endpoint, probedAt, RouteFailureReason.IdentityMismatch, "O DeviceId retornado pelo servidor diverge do host autorizado.");
                    }

                    return new RouteProbeResult
                    {
                        EndpointId = endpoint.EndpointId,
                        RouteKind = endpoint.RouteKind,
                        EndpointUrl = endpoint.EndpointUrl,
                        IsReachable = true,
                        LatencyMs = (int)Math.Max(1, sw.ElapsedMilliseconds),
                        ObservedServerDeviceId = observedId == Guid.Empty ? null : observedId,
                        ObservedServerName = observedName,
                        ObservedTlsThumbprint = info.TlsThumbprint,
                        ObservedHostKeyFingerprint = info.HostKeyFingerprint,
                        FailureReason = RouteFailureReason.None,
                        DiagnosticMessage = $"Rota {endpoint.RouteKind} alcançável em {Math.Max(1, sw.ElapsedMilliseconds)} ms.",
                        ProbedAtUtc = probedAt
                    };
                }
            }

            // Fallback to /health/live only when strict cryptographic identity pin is not required (e.g., initial unpinned endpoint test)
            if (string.IsNullOrWhiteSpace(endpoint.ExpectedHostKeyFingerprint))
            {
                using var liveResponse = await client.GetAsync("health/live", cancellationToken).ConfigureAwait(false);
                if (liveResponse.IsSuccessStatusCode)
                {
                    sw.Stop();
                    return new RouteProbeResult
                    {
                        EndpointId = endpoint.EndpointId,
                        RouteKind = endpoint.RouteKind,
                        EndpointUrl = endpoint.EndpointUrl,
                        IsReachable = true,
                        LatencyMs = (int)Math.Max(1, sw.ElapsedMilliseconds),
                        ObservedServerDeviceId = endpoint.DeviceId == Guid.Empty ? null : endpoint.DeviceId,
                        ObservedServerName = endpoint.DeviceName,
                        FailureReason = RouteFailureReason.None,
                        DiagnosticMessage = $"Endpoint respondeu via /health/live em {Math.Max(1, sw.ElapsedMilliseconds)} ms.",
                        ProbedAtUtc = probedAt
                    };
                }
            }

            return Fail(endpoint, probedAt, RouteFailureReason.HostUnreachable, $"Servidor respondeu HTTP {(int)response.StatusCode}.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Fail(endpoint, probedAt, RouteFailureReason.Cancelled, "Verificação de rota cancelada.");
        }
        catch (OperationCanceledException)
        {
            return Fail(
                endpoint,
                probedAt,
                endpoint.RouteKind == ConnectionRouteKind.RemoteVpnTailscale && networkEnvironment.VpnStatus != VpnStatus.Connected
                    ? RouteFailureReason.VpnUnavailable
                    : RouteFailureReason.Timeout,
                endpoint.RouteKind == ConnectionRouteKind.RemoteVpnTailscale && networkEnvironment.VpnStatus != VpnStatus.Connected
                    ? "Tempo esgotado ao alcançar Tailscale. Verifique se a VPN está conectada."
                    : "Tempo limite esgotado ao sondar endpoint.");
        }
        catch (AuthenticationException ex)
        {
            return Fail(endpoint, probedAt, RouteFailureReason.TlsPinMismatch, $"Falha na validação TLS/certificado: {ex.Message}");
        }
        catch (HttpRequestException ex)
        {
            if (ex.InnerException is AuthenticationException)
            {
                return Fail(endpoint, probedAt, RouteFailureReason.TlsPinMismatch, $"Certificado TLS rejeitado pelo pinning: {ex.InnerException.Message}");
            }

            if (endpoint.RouteKind == ConnectionRouteKind.RemoteVpnTailscale && networkEnvironment.VpnStatus != VpnStatus.Connected)
            {
                return Fail(endpoint, probedAt, RouteFailureReason.VpnUnavailable, "VPN Tailscale indisponível ou não conectada para alcançar este endereço privado.");
            }

            if (endpoint.RouteKind == ConnectionRouteKind.LocalLan && !networkEnvironment.HasLocalLanInterface)
            {
                return Fail(endpoint, probedAt, RouteFailureReason.LocalNetworkUnavailable, "Rede local indisponível para este endereço LAN.");
            }

            var isSocketRefused = ex.InnerException is SocketException;
            return Fail(
                endpoint,
                probedAt,
                RouteFailureReason.HostUnreachable,
                isSocketRefused
                    ? $"Servidor recusou ou não respondeu na rota {endpoint.EndpointUrl}."
                    : $"Falha de transporte: {ex.Message}");
        }
        catch (Exception ex)
        {
            return Fail(endpoint, probedAt, RouteFailureReason.HostUnreachable, ex.Message);
        }
    }

    private NexusResponse<ProbeSystemInfoDto>? TryDeserializeInfo(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            return _serializer.Deserialize<NexusResponse<ProbeSystemInfoDto>>(body);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsLoopbackHost(string host)
        => string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
           || (IPAddress.TryParse(host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));

    private static RouteProbeResult Fail(
        DeviceEndpointRecord endpoint,
        DateTimeOffset probedAt,
        RouteFailureReason reason,
        string message) => new()
        {
            EndpointId = endpoint.EndpointId,
            RouteKind = endpoint.RouteKind,
            EndpointUrl = endpoint.EndpointUrl,
            IsReachable = false,
            FailureReason = reason,
            DiagnosticMessage = message,
            ProbedAtUtc = probedAt
        };
}

