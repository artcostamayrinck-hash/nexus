using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Nexus.Contracts.Common;
using Nexus.Contracts.Connections;
using Nexus.Core.Abstractions;
using Nexus.Core.Connections;
using Nexus.Core.Errors;
using Nexus.Core.Events;

namespace Nexus.Infrastructure.Connectivity;

/// <summary>
/// Central NEXUS Connection Manager orchestrating local mDNS discovery, manual/Tailscale remote endpoints,
/// priority route selection (Local -> Remote/VPN), latency & health monitoring, automatic reconnect,
/// and non-disruptive route handover when operations are actively running.
/// </summary>
public sealed class NexusRouteOrchestrator : INexusRouteOrchestrator
{
    private sealed class OperationLease : IOperationLease
    {
        private readonly NexusRouteOrchestrator _owner;
        private int _disposed;

        public OperationLease(NexusRouteOrchestrator owner, string operationName, DateTimeOffset startedAtUtc)
        {
            _owner = owner;
            LeaseId = Guid.CreateVersion7();
            OperationName = operationName;
            StartedAtUtc = startedAtUtc;
        }

        public Guid LeaseId { get; }
        public string OperationName { get; }
        public DateTimeOffset StartedAtUtc { get; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _owner.ReleaseOperationLease(LeaseId);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private readonly IEndpointRegistryRepository _endpointRepository;
    private readonly IMdnsServiceDiscovery _mdnsDiscovery;
    private readonly INetworkEnvironmentInspector _networkInspector;
    private readonly IRouteProber _routeProber;
    private readonly IConnectionManager _connectionManager;
    private readonly IEventBus _eventBus;
    private readonly IClock _clock;
    private readonly ConnectivityOptions _options;
    private readonly ILogger<NexusRouteOrchestrator> _logger;

    private readonly SemaphoreSlim _selectionLock = new(1, 1);
    private readonly Lock _statusLock = new();
    private readonly ConcurrentDictionary<Guid, OperationLease> _activeOperations = new();

    private ConnectivityStatusReport _currentStatus;
    private IReadOnlyList<RouteProbeResult> _lastProbeResults = Array.Empty<RouteProbeResult>();
    private CancellationTokenSource? _monitorCts;
    private Task? _monitorTask;
    private bool _autoReconnectEnabled = true;
    private bool _handoverPendingOnIdle;
    private bool _disposed;

    public NexusRouteOrchestrator(
        IEndpointRegistryRepository endpointRepository,
        IMdnsServiceDiscovery mdnsDiscovery,
        INetworkEnvironmentInspector networkInspector,
        IRouteProber routeProber,
        IConnectionManager connectionManager,
        IEventBus eventBus,
        IClock clock,
        ILogger<NexusRouteOrchestrator> logger,
        ConnectivityOptions? options = null)
    {
        _endpointRepository = endpointRepository;
        _mdnsDiscovery = mdnsDiscovery;
        _networkInspector = networkInspector;
        _routeProber = routeProber;
        _connectionManager = connectionManager;
        _eventBus = eventBus;
        _clock = clock;
        _logger = logger;
        _options = options ?? new ConnectivityOptions();

        var net = _networkInspector.CurrentSnapshot;
        _currentStatus = new ConnectivityStatusReport
        {
            State = _connectionManager.CurrentSnapshot.State,
            VpnStatus = net.VpnStatus,
            VpnDetail = DescribeVpn(net),
            AutoReconnectEnabled = _autoReconnectEnabled,
            StatusReason = "Aguardando seleção de rota."
        };
    }

    public ConnectivityStatusReport CurrentStatus
    {
        get
        {
            lock (_statusLock)
            {
                return _currentStatus;
            }
        }
    }

    public event EventHandler<ConnectivityStatusReport>? StatusChanged;

    public async Task<Result<ConnectivityStatusReport>> InitializeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _networkInspector.RefreshAsync(cancellationToken).ConfigureAwait(false);
        var endpointsResult = await _endpointRepository.ListAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        if (endpointsResult.IsFailure)
        {
            return Result<ConnectivityStatusReport>.Failure(endpointsResult.Error!);
        }

        UpdateReport(BuildReport(
            _currentStatus.State,
            _currentStatus.DeviceName,
            _currentStatus.RemoteDeviceId,
            _currentStatus.ActiveEndpointId,
            _currentStatus.RouteKind,
            _currentStatus.Transport,
            _currentStatus.ActiveEndpoint,
            _currentStatus.LatencyMs,
            _currentStatus.LastConnectedAtUtc,
            _currentStatus.LastProbeAtUtc,
            _networkInspector.CurrentSnapshot,
            _currentStatus.LastFailureReason,
            _currentStatus.StatusReason,
            endpointsResult.Value,
            _lastProbeResults));

        if (_options.EnableBackgroundMonitor && (_monitorTask is null || _monitorTask.IsCompleted))
        {
            _monitorCts = new CancellationTokenSource();
            _monitorTask = Task.Run(() => RunMonitorLoopAsync(_monitorCts.Token), _monitorCts.Token);
        }

        return Result<ConnectivityStatusReport>.Success(CurrentStatus);
    }

    public async Task<Result<DeviceEndpointRecord>> RegisterEndpointAsync(
        RegisterEndpointRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var validation = ContractValidator.Validate(request);
        if (!validation.IsValid)
        {
            return Result<DeviceEndpointRecord>.Failure(NexusError.Validation(validation));
        }

        var normalizedUrl = request.EndpointUrl.Trim().TrimEnd('/');
        var classifiedKind = _networkInspector.ClassifyEndpoint(normalizedUrl);
        var effectiveKind = request.RouteKind == ConnectionRouteKind.Unknown ? classifiedKind : request.RouteKind;

        var now = _clock.UtcNow;
        var record = new DeviceEndpointRecord
        {
            EndpointId = Guid.CreateVersion7(),
            DeviceId = request.DeviceId,
            DeviceName = request.DeviceName.Trim(),
            Label = request.Label.Trim(),
            RouteKind = effectiveKind,
            Transport = MapTransport(effectiveKind),
            EndpointUrl = normalizedUrl,
            Priority = request.Priority ?? DeviceEndpointRecord.DefaultPriorityFor(effectiveKind),
            ExpectedTlsThumbprint = request.ExpectedTlsThumbprint,
            ExpectedHostKeyFingerprint = request.ExpectedHostKeyFingerprint,
            Origin = effectiveKind == ConnectionRouteKind.RemoteVpnTailscale && request.Origin == EndpointOrigin.Manual
                ? EndpointOrigin.TailscalePrivate
                : request.Origin,
            IsEnabled = true,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        var saved = await _endpointRepository.UpsertAsync(record, cancellationToken).ConfigureAwait(false);
        if (saved.IsFailure)
        {
            return saved;
        }

        await RefreshKnownEndpointsInReportAsync(cancellationToken).ConfigureAwait(false);
        return saved;
    }

    public async Task<Result> RemoveEndpointAsync(Guid endpointId, CancellationToken cancellationToken = default)
    {
        var result = await _endpointRepository.DeleteAsync(endpointId, cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return result;
        }

        if (CurrentStatus.ActiveEndpointId == endpointId)
        {
            await DisconnectAsync("Endpoint ativo removido pelo usuário.", cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await RefreshKnownEndpointsInReportAsync(cancellationToken).ConfigureAwait(false);
        }

        return Result.Success();
    }

    public async Task<Result<DeviceEndpointRecord>> SetEndpointEnabledAsync(
        Guid endpointId,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        var existing = await _endpointRepository.GetByIdAsync(endpointId, cancellationToken).ConfigureAwait(false);
        if (existing.IsFailure)
        {
            return Result<DeviceEndpointRecord>.Failure(existing.Error!);
        }

        if (existing.Value is null)
        {
            return Result<DeviceEndpointRecord>.Failure(NexusError.NotFound("Endpoint não encontrado.", "ENDPOINT_NOT_FOUND"));
        }

        var updated = existing.Value with { IsEnabled = enabled, UpdatedAtUtc = _clock.UtcNow };
        var saved = await _endpointRepository.UpsertAsync(updated, cancellationToken).ConfigureAwait(false);
        if (saved.IsSuccess)
        {
            await RefreshKnownEndpointsInReportAsync(cancellationToken).ConfigureAwait(false);
        }

        return saved;
    }

    public async Task<Result<IReadOnlyList<MdnsDiscoveredEndpoint>>> DiscoverLocalEndpointsAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var discoveryResult = await _mdnsDiscovery.DiscoverAsync(
            timeout ?? _options.MdnsDiscoveryTimeout,
            cancellationToken).ConfigureAwait(false);

        if (discoveryResult.IsFailure)
        {
            return discoveryResult;
        }

        foreach (var item in discoveryResult.Value)
        {
            await _endpointRepository.UpsertAsync(new DeviceEndpointRecord
            {
                EndpointId = Guid.CreateVersion7(),
                DeviceId = item.ServiceDeviceId,
                DeviceName = item.ServiceName,
                Label = $"LAN mDNS ({item.HostName})",
                RouteKind = ConnectionRouteKind.LocalLan,
                Transport = TransportKind.LocalNetwork,
                EndpointUrl = item.EndpointUrl.Trim().TrimEnd('/'),
                Priority = DeviceEndpointRecord.DefaultPriorityFor(ConnectionRouteKind.LocalLan),
                ExpectedTlsThumbprint = item.TlsThumbprint,
                ExpectedHostKeyFingerprint = item.HostKeyFingerprint,
                Origin = EndpointOrigin.MdnsDiscovered,
                IsEnabled = true,
                LastVerifiedAtUtc = item.DiscoveredAtUtc,
                CreatedAtUtc = item.DiscoveredAtUtc,
                UpdatedAtUtc = item.DiscoveredAtUtc
            }, cancellationToken).ConfigureAwait(false);
        }

        await RefreshKnownEndpointsInReportAsync(cancellationToken).ConfigureAwait(false);
        return discoveryResult;
    }

    public async Task<Result<IReadOnlyList<RouteProbeResult>>> ProbeAllEndpointsAsync(CancellationToken cancellationToken = default)
    {
        var net = await _networkInspector.RefreshAsync(cancellationToken).ConfigureAwait(false);
        var listResult = await _endpointRepository.ListAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        if (listResult.IsFailure)
        {
            return Result<IReadOnlyList<RouteProbeResult>>.Failure(listResult.Error!);
        }

        var enabled = OrderByRoutePriority(listResult.Value.Where(e => e.IsEnabled));
        var results = new List<RouteProbeResult>(enabled.Count);

        foreach (var endpoint in enabled)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var probe = await _routeProber.ProbeAsync(endpoint, net, cancellationToken).ConfigureAwait(false);
            results.Add(probe);

            await _endpointRepository.UpdateTelemetryAsync(
                endpoint.EndpointId,
                probe.IsReachable ? probe.LatencyMs : null,
                probe.ProbedAtUtc,
                null,
                probe.IsReachable ? null : probe.DiagnosticMessage,
                cancellationToken).ConfigureAwait(false);
        }

        _lastProbeResults = results.AsReadOnly();
        return Result<IReadOnlyList<RouteProbeResult>>.Success(_lastProbeResults);
    }

    public async Task<Result<ConnectivityStatusReport>> SelectAndConnectBestRouteAsync(
        SelectRouteRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        request ??= new SelectRouteRequest();
        await _selectionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var net = await _networkInspector.RefreshAsync(cancellationToken).ConfigureAwait(false);
            var listResult = await _endpointRepository.ListAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            if (listResult.IsFailure)
            {
                return Result<ConnectivityStatusReport>.Failure(listResult.Error!);
            }

            var allEndpoints = listResult.Value;
            var candidates = OrderByRoutePriority(allEndpoints.Where(e => e.IsEnabled), request.PreferredEndpointId);

            if (candidates.Count == 0)
            {
                const string noEpMsg = "Nenhum endpoint configurado. Adicione um endereço LAN ou Tailscale, ou execute a descoberta local.";
                await _connectionManager.TransitionAsync(
                    ConnectionState.Faulted,
                    TransportKind.Unknown,
                    reason: noEpMsg,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                var emptyReport = BuildReport(
                    ConnectionState.Faulted,
                    "Nenhum dispositivo",
                    null,
                    null,
                    ConnectionRouteKind.Unknown,
                    TransportKind.Unknown,
                    null,
                    null,
                    CurrentStatus.LastConnectedAtUtc,
                    _clock.UtcNow,
                    net,
                    RouteFailureReason.NoEndpointsConfigured,
                    noEpMsg,
                    allEndpoints,
                    Array.Empty<RouteProbeResult>());

                UpdateReport(emptyReport);
                return Result<ConnectivityStatusReport>.Failure(NexusError.Validation(noEpMsg, code: "NO_ENDPOINTS_CONFIGURED"));
            }

            // Check active in-flight operations BEFORE switching routes while still connected on a healthy current route!
            var current = CurrentStatus;
            var hasActiveOps = !_activeOperations.IsEmpty;
            if (current.State == ConnectionState.Connected
                && current.ActiveEndpointId.HasValue
                && hasActiveOps
                && !request.AllowInterruptActiveOperations)
            {
                var currentEndpoint = candidates.FirstOrDefault(e => e.EndpointId == current.ActiveEndpointId.Value);
                if (currentEndpoint is not null)
                {
                    var currentProbe = await _routeProber.ProbeAsync(currentEndpoint, net, cancellationToken).ConfigureAwait(false);
                    if (currentProbe.IsReachable)
                    {
                        var topCandidate = candidates.First();
                        if (topCandidate.EndpointId != currentEndpoint.EndpointId)
                        {
                            _handoverPendingOnIdle = true;
                            await _eventBus.PublishAsync(
                                new ConnectivityHandoverDeferredDomainEvent(
                                    currentEndpoint.EndpointUrl,
                                    topCandidate.EndpointUrl,
                                    _activeOperations.Count),
                                cancellationToken).ConfigureAwait(false);

                            var deferredReport = current with
                            {
                                LatencyMs = currentProbe.LatencyMs,
                                LastProbeAtUtc = currentProbe.ProbedAtUtc,
                                VpnStatus = net.VpnStatus,
                                VpnDetail = DescribeVpn(net),
                                ActiveOperationsCount = _activeOperations.Count,
                                HandoverDeferred = true,
                                StatusReason = $"Operação ativa ({_activeOperations.Count}): alternância para rota prioritária aguardará conclusão."
                            };
                            UpdateReport(deferredReport);
                            return Result<ConnectivityStatusReport>.Success(deferredReport);
                        }
                    }
                }
            }

            await _connectionManager.TransitionAsync(
                current.State == ConnectionState.Connected ? ConnectionState.Reconnecting : ConnectionState.Connecting,
                candidates[0].Transport,
                candidates[0].EndpointUrl,
                candidates[0].DeviceId == Guid.Empty ? null : candidates[0].DeviceId,
                reason: "Avaliando rotas disponíveis (Local -> Remoto/VPN)...",
                cancellationToken: cancellationToken).ConfigureAwait(false);

            var probes = new List<RouteProbeResult>(candidates.Count);
            DeviceEndpointRecord? chosen = null;
            RouteProbeResult? chosenProbe = null;

            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var probe = await _routeProber.ProbeAsync(candidate, net, cancellationToken).ConfigureAwait(false);
                probes.Add(probe);

                await _endpointRepository.UpdateTelemetryAsync(
                    candidate.EndpointId,
                    probe.IsReachable ? probe.LatencyMs : null,
                    probe.ProbedAtUtc,
                    probe.IsReachable ? probe.ProbedAtUtc : null,
                    probe.IsReachable ? null : probe.DiagnosticMessage,
                    cancellationToken).ConfigureAwait(false);

                if (probe.IsReachable)
                {
                    chosen = candidate;
                    chosenProbe = probe;
                    break;
                }
            }

            _lastProbeResults = probes.AsReadOnly();
            var refreshedList = (await _endpointRepository.ListAsync(cancellationToken: cancellationToken).ConfigureAwait(false));
            var latestEndpoints = refreshedList.IsSuccess ? refreshedList.Value : allEndpoints;

            if (chosen is not null && chosenProbe is not null)
            {
                _handoverPendingOnIdle = false;
                var effectiveDeviceId = chosenProbe.ObservedServerDeviceId ?? (chosen.DeviceId == Guid.Empty ? null : chosen.DeviceId);
                var effectiveName = !string.IsNullOrWhiteSpace(chosenProbe.ObservedServerName)
                    ? chosenProbe.ObservedServerName
                    : chosen.DeviceName;

                var routeLabel = chosen.RouteKind switch
                {
                    ConnectionRouteKind.LocalLan => "Local (Wi-Fi / LAN)",
                    ConnectionRouteKind.RemoteVpnTailscale => "Remoto (VPN Tailscale)",
                    ConnectionRouteKind.RemotePrivate => "Remoto Privado",
                    _ => chosen.RouteKind.ToString()
                };

                var reasonText = $"Conectado via {routeLabel} ({chosenProbe.LatencyMs} ms).";
                await _connectionManager.TransitionAsync(
                    ConnectionState.Connected,
                    chosen.Transport,
                    chosen.EndpointUrl,
                    effectiveDeviceId,
                    chosenProbe.LatencyMs,
                    reasonText,
                    cancellationToken).ConfigureAwait(false);

                var connectedReport = BuildReport(
                    ConnectionState.Connected,
                    effectiveName,
                    effectiveDeviceId,
                    chosen.EndpointId,
                    chosen.RouteKind,
                    chosen.Transport,
                    chosen.EndpointUrl,
                    chosenProbe.LatencyMs,
                    chosenProbe.ProbedAtUtc,
                    chosenProbe.ProbedAtUtc,
                    net,
                    RouteFailureReason.None,
                    reasonText,
                    latestEndpoints,
                    _lastProbeResults);

                UpdateReport(connectedReport);
                await _eventBus.PublishAsync(new RouteSelectedDomainEvent(connectedReport), cancellationToken).ConfigureAwait(false);
                return Result<ConnectivityStatusReport>.Success(connectedReport);
            }

            // No route succeeded: synthesize actionable failure explanation
            var (failureReason, summaryReason) = SummarizeFailure(net, probes);
            var targetState = _autoReconnectEnabled ? ConnectionState.Reconnecting : ConnectionState.Faulted;

            await _connectionManager.TransitionAsync(
                targetState,
                candidates[0].Transport,
                candidates[0].EndpointUrl,
                reason: summaryReason,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            var failedReport = BuildReport(
                targetState,
                candidates[0].DeviceName,
                candidates[0].DeviceId == Guid.Empty ? null : candidates[0].DeviceId,
                null,
                ConnectionRouteKind.Unknown,
                TransportKind.Unknown,
                null,
                null,
                CurrentStatus.LastConnectedAtUtc,
                _clock.UtcNow,
                net,
                failureReason,
                summaryReason,
                latestEndpoints,
                _lastProbeResults);

            UpdateReport(failedReport);
            return Result<ConnectivityStatusReport>.Failure(
                NexusError.Network(summaryReason, failureReason.ToString().ToUpperInvariant()));
        }
        finally
        {
            _selectionLock.Release();
        }
    }

    public async Task<Result<ConnectivityStatusReport>> HandleNetworkChangedAsync(CancellationToken cancellationToken = default)
    {
        return await SelectAndConnectBestRouteAsync(
            new SelectRouteRequest { ForceReprobe = true, AllowInterruptActiveOperations = false },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<ConnectivityStatusReport>> DisconnectAsync(
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        await _selectionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _handoverPendingOnIdle = false;
            var msg = string.IsNullOrWhiteSpace(reason) ? "Desconectado manualmente." : reason;
            await _connectionManager.DisconnectAsync(msg, cancellationToken).ConfigureAwait(false);
            var net = await _networkInspector.RefreshAsync(cancellationToken).ConfigureAwait(false);
            var list = await _endpointRepository.ListAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

            var report = BuildReport(
                ConnectionState.Disconnected,
                CurrentStatus.DeviceName,
                CurrentStatus.RemoteDeviceId,
                null,
                ConnectionRouteKind.Unknown,
                TransportKind.Unknown,
                null,
                null,
                CurrentStatus.LastConnectedAtUtc,
                _clock.UtcNow,
                net,
                RouteFailureReason.None,
                msg,
                list.IsSuccess ? list.Value : Array.Empty<DeviceEndpointRecord>(),
                _lastProbeResults);

            UpdateReport(report);
            return Result<ConnectivityStatusReport>.Success(report);
        }
        finally
        {
            _selectionLock.Release();
        }
    }

    public void SetAutoReconnect(bool enabled)
    {
        _autoReconnectEnabled = enabled;
        UpdateReport(CurrentStatus with { AutoReconnectEnabled = enabled });
    }

    public Task<Result<IOperationLease>> BeginOperationScopeAsync(
        string operationName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var lease = new OperationLease(this, string.IsNullOrWhiteSpace(operationName) ? "operation" : operationName, _clock.UtcNow);
        _activeOperations[lease.LeaseId] = lease;
        UpdateReport(CurrentStatus with { ActiveOperationsCount = _activeOperations.Count });
        return Task.FromResult(Result<IOperationLease>.Success(lease));
    }

    private void ReleaseOperationLease(Guid leaseId)
    {
        _activeOperations.TryRemove(leaseId, out _);
        var count = _activeOperations.Count;
        var shouldPromote = count == 0 && _handoverPendingOnIdle;
        UpdateReport(CurrentStatus with { ActiveOperationsCount = count, HandoverDeferred = !shouldPromote && _handoverPendingOnIdle });

        if (shouldPromote && !_disposed)
        {
            _handoverPendingOnIdle = false;
            _ = Task.Run(async () =>
            {
                try
                {
                    await SelectAndConnectBestRouteAsync(new SelectRouteRequest { AllowInterruptActiveOperations = false })
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Deferred route promotion failed.");
                }
            });
        }
    }

    private async Task RunMonitorLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_options.MonitorInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await MonitorTickAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Connectivity monitor tick encountered an error.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
    }

    private async Task MonitorTickAsync(CancellationToken ct)
    {
        var net = await _networkInspector.RefreshAsync(ct).ConfigureAwait(false);
        var status = CurrentStatus;

        // Do not auto-connect if user explicitly paused/disconnected? Wait: only reconnect automatically when
        // AutoReconnectEnabled is true AND state is Reconnecting/Faulted, OR verify health if Connected!
        if (status.State == ConnectionState.Connected && status.ActiveEndpointId.HasValue)
        {
            var epResult = await _endpointRepository.GetByIdAsync(status.ActiveEndpointId.Value, ct).ConfigureAwait(false);
            var activeEp = epResult.IsSuccess ? epResult.Value : null;
            if (activeEp is null)
            {
                await SelectAndConnectBestRouteAsync(cancellationToken: ct).ConfigureAwait(false);
                return;
            }

            var probe = await _routeProber.ProbeAsync(activeEp, net, ct).ConfigureAwait(false);
            if (!probe.IsReachable)
            {
                // Current active route dropped! Failover immediately (or enter Reconnecting).
                _logger.LogWarning("Active route {Endpoint} lost connectivity ({Reason}); attempting failover.", activeEp.EndpointUrl, probe.FailureReason);
                await SelectAndConnectBestRouteAsync(new SelectRouteRequest { AllowInterruptActiveOperations = true }, ct).ConfigureAwait(false);
                return;
            }

            // Active route healthy: update latency OR gently promote higher-priority LocalLan if idle!
            await _endpointRepository.UpdateTelemetryAsync(activeEp.EndpointId, probe.LatencyMs, probe.ProbedAtUtc, null, null, ct).ConfigureAwait(false);
            if (activeEp.RouteKind != ConnectionRouteKind.LocalLan && _activeOperations.IsEmpty)
            {
                var all = await _endpointRepository.ListAsync(cancellationToken: ct).ConfigureAwait(false);
                var lanCandidates = all.IsSuccess
                    ? all.Value.Where(e => e.IsEnabled && e.RouteKind == ConnectionRouteKind.LocalLan)
                    : Enumerable.Empty<DeviceEndpointRecord>();

                foreach (var lanCandidate in lanCandidates)
                {
                    var lanProbe = await _routeProber.ProbeAsync(lanCandidate, net, ct).ConfigureAwait(false);
                    if (lanProbe.IsReachable)
                    {
                        await SelectAndConnectBestRouteAsync(cancellationToken: ct).ConfigureAwait(false);
                        return;
                    }
                }
            }

            UpdateReport(status with
            {
                LatencyMs = probe.LatencyMs,
                LastProbeAtUtc = probe.ProbedAtUtc,
                VpnStatus = net.VpnStatus,
                VpnDetail = DescribeVpn(net)
            });
        }
        else if (_autoReconnectEnabled && status.State is ConnectionState.Reconnecting or ConnectionState.Faulted)
        {
            await SelectAndConnectBestRouteAsync(cancellationToken: ct).ConfigureAwait(false);
        }
    }

    private async Task RefreshKnownEndpointsInReportAsync(CancellationToken cancellationToken)
    {
        var list = await _endpointRepository.ListAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        if (list.IsSuccess)
        {
            UpdateReport(CurrentStatus with { KnownEndpoints = list.Value });
        }
    }

    private ConnectivityStatusReport BuildReport(
        ConnectionState state,
        string deviceName,
        Guid? remoteDeviceId,
        Guid? activeEndpointId,
        ConnectionRouteKind routeKind,
        TransportKind transport,
        string? activeEndpoint,
        int? latencyMs,
        DateTimeOffset? lastConnectedAtUtc,
        DateTimeOffset? lastProbeAtUtc,
        NetworkEnvironmentSnapshot net,
        RouteFailureReason failureReason,
        string? statusReason,
        IReadOnlyList<DeviceEndpointRecord> knownEndpoints,
        IReadOnlyList<RouteProbeResult> lastProbes)
    {
        return new ConnectivityStatusReport
        {
            State = state,
            DeviceName = string.IsNullOrWhiteSpace(deviceName) ? "Nenhum dispositivo" : deviceName,
            RemoteDeviceId = remoteDeviceId,
            ActiveEndpointId = activeEndpointId,
            RouteKind = routeKind,
            Transport = transport,
            ActiveEndpoint = activeEndpoint,
            LatencyMs = latencyMs,
            LastConnectedAtUtc = lastConnectedAtUtc,
            LastProbeAtUtc = lastProbeAtUtc,
            VpnStatus = net.VpnStatus,
            VpnDetail = DescribeVpn(net),
            LastFailureReason = failureReason,
            StatusReason = statusReason,
            AutoReconnectEnabled = _autoReconnectEnabled,
            ActiveOperationsCount = _activeOperations.Count,
            HandoverDeferred = _handoverPendingOnIdle,
            KnownEndpoints = knownEndpoints,
            LastProbeResults = lastProbes
        };
    }

    private void UpdateReport(ConnectivityStatusReport next)
    {
        lock (_statusLock)
        {
            _currentStatus = next;
        }

        StatusChanged?.Invoke(this, next);
    }

    private static IReadOnlyList<DeviceEndpointRecord> OrderByRoutePriority(
        IEnumerable<DeviceEndpointRecord> endpoints,
        Guid? preferredEndpointId = null)
    {
        return endpoints
            .OrderBy(e => preferredEndpointId.HasValue && e.EndpointId == preferredEndpointId.Value ? 0 : 1)
            .ThenBy(e => RouteTier(e.RouteKind))
            .ThenBy(e => e.Priority)
            .ThenBy(e => e.LastLatencyMs ?? int.MaxValue)
            .ToList()
            .AsReadOnly();
    }

    private static int RouteTier(ConnectionRouteKind kind) => kind switch
    {
        ConnectionRouteKind.LocalLan => 1,
        ConnectionRouteKind.RemoteVpnTailscale => 2,
        ConnectionRouteKind.RemotePrivate => 3,
        ConnectionRouteKind.RelayFuture => 4,
        _ => 9
    };

    private static TransportKind MapTransport(ConnectionRouteKind kind) => kind switch
    {
        ConnectionRouteKind.LocalLan => TransportKind.LocalNetwork,
        ConnectionRouteKind.RemoteVpnTailscale => TransportKind.SignalR,
        ConnectionRouteKind.RemotePrivate => TransportKind.WebSocket,
        _ => TransportKind.Unknown
    };

    private static (RouteFailureReason PrimaryReason, string Summary) SummarizeFailure(
        NetworkEnvironmentSnapshot net,
        IReadOnlyList<RouteProbeResult> probes)
    {
        if (!net.IsNetworkAvailable && probes.Any(p => p.FailureReason == RouteFailureReason.InternetUnavailable))
        {
            return (RouteFailureReason.InternetUnavailable, "Sem conexão com a rede ou internet. Reconecte o Wi-Fi ou dados móveis.");
        }

        if (probes.Any(p => p.FailureReason == RouteFailureReason.TlsPinMismatch))
        {
            return (RouteFailureReason.TlsPinMismatch, "Falha de segurança: certificado TLS do endpoint não confere com o pareamento.");
        }

        if (probes.Any(p => p.FailureReason == RouteFailureReason.IdentityMismatch))
        {
            return (RouteFailureReason.IdentityMismatch, "Falha de identidade: a chave criptográfica do host não confere.");
        }

        if (probes.Any(p => p.FailureReason == RouteFailureReason.DeviceRevoked))
        {
            return (RouteFailureReason.DeviceRevoked, "Dispositivo revogado no servidor remoto.");
        }

        var vpnFail = probes.FirstOrDefault(p => p.FailureReason == RouteFailureReason.VpnUnavailable);
        var lanFail = probes.FirstOrDefault(p => p.FailureReason == RouteFailureReason.LocalNetworkUnavailable);

        if (vpnFail is not null && lanFail is not null)
        {
            return (RouteFailureReason.VpnUnavailable, "Rede local indisponível e VPN Tailscale desconectada. Ative o Tailscale para conexão remota.");
        }

        if (vpnFail is not null)
        {
            return (RouteFailureReason.VpnUnavailable, vpnFail.DiagnosticMessage ?? "VPN Tailscale indisponível para o endpoint remoto.");
        }

        if (probes.All(p => p.FailureReason == RouteFailureReason.InvalidEndpoint))
        {
            return (RouteFailureReason.InvalidEndpoint, "Endpoint configurado inválido.");
        }

        var first = probes.FirstOrDefault();
        return (first?.FailureReason ?? RouteFailureReason.HostUnreachable,
            first?.DiagnosticMessage ?? "Nenhuma rota local ou remota respondeu.");
    }

    private static string DescribeVpn(NetworkEnvironmentSnapshot net) => net.VpnStatus switch
    {
        VpnStatus.Connected => net.TailscaleAddresses.Count > 0
            ? $"Tailscale ativo ({string.Join(", ", net.TailscaleAddresses)})"
            : "Tailscale conectado",
        VpnStatus.Disconnected => "Tailscale desconectado",
        VpnStatus.Unavailable => "Tailscale não detectado",
        _ => "Estado VPN desconhecido"
    };

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_monitorCts is not null)
        {
            await _monitorCts.CancelAsync().ConfigureAwait(false);
            _monitorCts.Dispose();
        }

        if (_monitorTask is not null)
        {
            try
            {
                await _monitorTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch
            {
                // Ignore monitor cancellation errors during teardown
            }
        }

        _selectionLock.Dispose();
    }
}
