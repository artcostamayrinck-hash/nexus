using Microsoft.Extensions.Logging;
using Nexus.Contracts.Modules;
using Nexus.Contracts.Security;
using Nexus.Core.Abstractions;
using Nexus.Core.Errors;
using Nexus.Core.Modules;
using Nexus.Core.Storage;

namespace Nexus.Infrastructure.Modules;

/// <summary>
/// Persists independent settings and enablement states for each Fase 08 integration module.
/// </summary>
public sealed class ModuleSettingsService : IModuleSettingsService
{
    public const string ControlSettingsKey = "nexus.modules.control.v1";
    public const string NotifySettingsKey = "nexus.modules.notify.v1";
    public const string MonitorSettingsKey = "nexus.modules.monitor.v1";
    public const string MediaSettingsKey = "nexus.modules.media.v1";

    private readonly ISettingsStore _settingsStore;
    private readonly IClock _clock;
    private readonly ILogger<ModuleSettingsService> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private ControlModuleSettings? _controlCache;
    private NotifyModuleSettings? _notifyCache;
    private MonitorModuleSettings? _monitorCache;
    private MediaModuleSettings? _mediaCache;

    public ModuleSettingsService(
        ISettingsStore settingsStore,
        IClock clock,
        ILogger<ModuleSettingsService> logger)
    {
        _settingsStore = settingsStore;
        _clock = clock;
        _logger = logger;
    }

    public event EventHandler<IntegrationModuleKind>? ModuleSettingsChanged;

    public async Task<Result<IReadOnlyList<IntegrationModuleStatusSnapshot>>> GetModuleStatusesAsync(
        CancellationToken cancellationToken = default)
    {
        var control = await GetControlSettingsAsync(cancellationToken).ConfigureAwait(false);
        var notify = await GetNotifySettingsAsync(cancellationToken).ConfigureAwait(false);
        var monitor = await GetMonitorSettingsAsync(cancellationToken).ConfigureAwait(false);
        var media = await GetMediaSettingsAsync(cancellationToken).ConfigureAwait(false);

        if (control.IsFailure) return Result<IReadOnlyList<IntegrationModuleStatusSnapshot>>.Failure(control.Error!);
        if (notify.IsFailure) return Result<IReadOnlyList<IntegrationModuleStatusSnapshot>>.Failure(notify.Error!);
        if (monitor.IsFailure) return Result<IReadOnlyList<IntegrationModuleStatusSnapshot>>.Failure(monitor.Error!);
        if (media.IsFailure) return Result<IReadOnlyList<IntegrationModuleStatusSnapshot>>.Failure(media.Error!);

        var isWindows = OperatingSystem.IsWindows();
        var list = new List<IntegrationModuleStatusSnapshot>
        {
            BuildStatus(IntegrationModuleKind.Control, control.Value.IsEnabled, control.Value.RequirePowerActionConfirmation, isWindows, control.Value.UpdatedAtUtc),
            BuildStatus(IntegrationModuleKind.Notify, notify.Value.IsEnabled, false, true, notify.Value.UpdatedAtUtc),
            BuildStatus(IntegrationModuleKind.Monitor, monitor.Value.IsEnabled, false, isWindows, monitor.Value.UpdatedAtUtc),
            BuildStatus(IntegrationModuleKind.Media, media.Value.IsEnabled, false, isWindows, media.Value.UpdatedAtUtc)
        };

        return Result<IReadOnlyList<IntegrationModuleStatusSnapshot>>.Success(list.AsReadOnly());
    }

    public async Task<Result<IntegrationModuleStatusSnapshot>> SetModuleEnabledAsync(
        UpdateModuleToggleRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        switch (request.ModuleKind)
        {
            case IntegrationModuleKind.Control:
            {
                var cur = await GetControlSettingsAsync(cancellationToken).ConfigureAwait(false);
                if (cur.IsFailure) return Result<IntegrationModuleStatusSnapshot>.Failure(cur.Error!);
                var updated = cur.Value with
                {
                    IsEnabled = request.IsEnabled,
                    RequirePowerActionConfirmation = request.RequireConfirmationForCriticalActions ?? cur.Value.RequirePowerActionConfirmation
                };
                var saved = await SaveControlSettingsAsync(updated, cancellationToken).ConfigureAwait(false);
                if (saved.IsFailure) return Result<IntegrationModuleStatusSnapshot>.Failure(saved.Error!);
                return Result<IntegrationModuleStatusSnapshot>.Success(
                    BuildStatus(IntegrationModuleKind.Control, saved.Value.IsEnabled, saved.Value.RequirePowerActionConfirmation, OperatingSystem.IsWindows(), saved.Value.UpdatedAtUtc));
            }
            case IntegrationModuleKind.Notify:
            {
                var cur = await GetNotifySettingsAsync(cancellationToken).ConfigureAwait(false);
                if (cur.IsFailure) return Result<IntegrationModuleStatusSnapshot>.Failure(cur.Error!);
                var saved = await SaveNotifySettingsAsync(cur.Value with { IsEnabled = request.IsEnabled }, cancellationToken).ConfigureAwait(false);
                if (saved.IsFailure) return Result<IntegrationModuleStatusSnapshot>.Failure(saved.Error!);
                return Result<IntegrationModuleStatusSnapshot>.Success(
                    BuildStatus(IntegrationModuleKind.Notify, saved.Value.IsEnabled, false, true, saved.Value.UpdatedAtUtc));
            }
            case IntegrationModuleKind.Monitor:
            {
                var cur = await GetMonitorSettingsAsync(cancellationToken).ConfigureAwait(false);
                if (cur.IsFailure) return Result<IntegrationModuleStatusSnapshot>.Failure(cur.Error!);
                var saved = await SaveMonitorSettingsAsync(cur.Value with { IsEnabled = request.IsEnabled }, cancellationToken).ConfigureAwait(false);
                if (saved.IsFailure) return Result<IntegrationModuleStatusSnapshot>.Failure(saved.Error!);
                return Result<IntegrationModuleStatusSnapshot>.Success(
                    BuildStatus(IntegrationModuleKind.Monitor, saved.Value.IsEnabled, false, OperatingSystem.IsWindows(), saved.Value.UpdatedAtUtc));
            }
            case IntegrationModuleKind.Media:
            {
                var cur = await GetMediaSettingsAsync(cancellationToken).ConfigureAwait(false);
                if (cur.IsFailure) return Result<IntegrationModuleStatusSnapshot>.Failure(cur.Error!);
                var saved = await SaveMediaSettingsAsync(cur.Value with { IsEnabled = request.IsEnabled }, cancellationToken).ConfigureAwait(false);
                if (saved.IsFailure) return Result<IntegrationModuleStatusSnapshot>.Failure(saved.Error!);
                return Result<IntegrationModuleStatusSnapshot>.Success(
                    BuildStatus(IntegrationModuleKind.Media, saved.Value.IsEnabled, false, OperatingSystem.IsWindows(), saved.Value.UpdatedAtUtc));
            }
            default:
                return Result<IntegrationModuleStatusSnapshot>.Failure(
                    NexusError.Validation($"Módulo desconhecido: {request.ModuleKind}", code: "MODULE_UNKNOWN"));
        }
    }

    public async Task<Result<ControlModuleSettings>> GetControlSettingsAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_controlCache is not null)
            {
                return Result<ControlModuleSettings>.Success(_controlCache);
            }

            var res = await _settingsStore
                .GetOrDefaultAsync(ControlSettingsKey, new ControlModuleSettings(), cancellationToken)
                .ConfigureAwait(false);
            if (res.IsFailure) return res;

            _controlCache = res.Value;
            return Result<ControlModuleSettings>.Success(_controlCache);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<Result<ControlModuleSettings>> SaveControlSettingsAsync(
        ControlModuleSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        ControlModuleSettings updated;
        try
        {
            updated = settings with
            {
                Hotkeys = settings.Hotkeys is { Count: > 0 } ? settings.Hotkeys : ControlModuleSettings.DefaultHotkeys,
                UpdatedAtUtc = _clock.UtcNow
            };
            var saved = await _settingsStore.SetAsync(ControlSettingsKey, updated, cancellationToken).ConfigureAwait(false);
            if (saved.IsFailure) return Result<ControlModuleSettings>.Failure(saved.Error!);
            _controlCache = updated;
        }
        finally
        {
            _lock.Release();
        }

        _logger.LogInformation("Nexus Control settings saved (Enabled={Enabled}, ConfirmPower={Confirm})", updated.IsEnabled, updated.RequirePowerActionConfirmation);
        ModuleSettingsChanged?.Invoke(this, IntegrationModuleKind.Control);
        return Result<ControlModuleSettings>.Success(updated);
    }

    public async Task<Result<NotifyModuleSettings>> GetNotifySettingsAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_notifyCache is not null)
            {
                return Result<NotifyModuleSettings>.Success(_notifyCache);
            }

            var res = await _settingsStore
                .GetOrDefaultAsync(NotifySettingsKey, new NotifyModuleSettings(), cancellationToken)
                .ConfigureAwait(false);
            if (res.IsFailure) return res;

            _notifyCache = res.Value;
            return Result<NotifyModuleSettings>.Success(_notifyCache);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<Result<NotifyModuleSettings>> SaveNotifySettingsAsync(
        NotifyModuleSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        NotifyModuleSettings updated;
        try
        {
            updated = settings with
            {
                AllowedApps = NormalizeAppList(settings.AllowedApps),
                BlockedApps = NormalizeAppList(settings.BlockedApps),
                UpdatedAtUtc = _clock.UtcNow
            };
            var saved = await _settingsStore.SetAsync(NotifySettingsKey, updated, cancellationToken).ConfigureAwait(false);
            if (saved.IsFailure) return Result<NotifyModuleSettings>.Failure(saved.Error!);
            _notifyCache = updated;
        }
        finally
        {
            _lock.Release();
        }

        _logger.LogInformation("Nexus Notify settings saved (Enabled={Enabled}, FilterMode={FilterMode})", updated.IsEnabled, updated.FilterMode);
        ModuleSettingsChanged?.Invoke(this, IntegrationModuleKind.Notify);
        return Result<NotifyModuleSettings>.Success(updated);
    }

    public async Task<Result<MonitorModuleSettings>> GetMonitorSettingsAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_monitorCache is not null)
            {
                return Result<MonitorModuleSettings>.Success(_monitorCache);
            }

            var res = await _settingsStore
                .GetOrDefaultAsync(MonitorSettingsKey, new MonitorModuleSettings(), cancellationToken)
                .ConfigureAwait(false);
            if (res.IsFailure) return res;

            _monitorCache = res.Value;
            return Result<MonitorModuleSettings>.Success(_monitorCache);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<Result<MonitorModuleSettings>> SaveMonitorSettingsAsync(
        MonitorModuleSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        MonitorModuleSettings updated;
        try
        {
            updated = settings with
            {
                RefreshIntervalSeconds = Math.Clamp(settings.RefreshIntervalSeconds, 1, 60),
                MaxProcesses = Math.Clamp(settings.MaxProcesses, 1, 50),
                UpdatedAtUtc = _clock.UtcNow
            };
            var saved = await _settingsStore.SetAsync(MonitorSettingsKey, updated, cancellationToken).ConfigureAwait(false);
            if (saved.IsFailure) return Result<MonitorModuleSettings>.Failure(saved.Error!);
            _monitorCache = updated;
        }
        finally
        {
            _lock.Release();
        }

        _logger.LogInformation("Nexus Monitor settings saved (Enabled={Enabled}, RefreshSec={Refresh})", updated.IsEnabled, updated.RefreshIntervalSeconds);
        ModuleSettingsChanged?.Invoke(this, IntegrationModuleKind.Monitor);
        return Result<MonitorModuleSettings>.Success(updated);
    }

    public async Task<Result<MediaModuleSettings>> GetMediaSettingsAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_mediaCache is not null)
            {
                return Result<MediaModuleSettings>.Success(_mediaCache);
            }

            var res = await _settingsStore
                .GetOrDefaultAsync(MediaSettingsKey, new MediaModuleSettings(), cancellationToken)
                .ConfigureAwait(false);
            if (res.IsFailure) return res;

            _mediaCache = res.Value;
            return Result<MediaModuleSettings>.Success(_mediaCache);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<Result<MediaModuleSettings>> SaveMediaSettingsAsync(
        MediaModuleSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        MediaModuleSettings updated;
        try
        {
            updated = settings with { UpdatedAtUtc = _clock.UtcNow };
            var saved = await _settingsStore.SetAsync(MediaSettingsKey, updated, cancellationToken).ConfigureAwait(false);
            if (saved.IsFailure) return Result<MediaModuleSettings>.Failure(saved.Error!);
            _mediaCache = updated;
        }
        finally
        {
            _lock.Release();
        }

        _logger.LogInformation("Nexus Media settings saved (Enabled={Enabled})", updated.IsEnabled);
        ModuleSettingsChanged?.Invoke(this, IntegrationModuleKind.Media);
        return Result<MediaModuleSettings>.Success(updated);
    }

    private static IntegrationModuleStatusSnapshot BuildStatus(
        IntegrationModuleKind kind,
        bool isEnabled,
        bool confirmCritical,
        bool osSupported,
        DateTimeOffset updatedAtUtc)
    {
        var (id, name, desc, perm, availMsg) = kind switch
        {
            IntegrationModuleKind.Control => (
                "nexus.control",
                "Nexus Control",
                "Controle remoto de mouse, teclado, atalhos, volume, mídia e ações de energia.",
                NexusPermission.ControlInput,
                osSupported ? "Operacional via API Win32/CoreAudio" : "Execução nativa disponível apenas no host Windows"),
            IntegrationModuleKind.Notify => (
                "nexus.notify",
                "Nexus Notify",
                "Encaminhamento seguro de notificações Android com filtro por aplicativo.",
                NexusPermission.Notifications,
                "Pronto (Android requer permissão NotificationListenerService)"),
            IntegrationModuleKind.Monitor => (
                "nexus.monitor",
                "Nexus Monitor",
                "Telemetria real de CPU, RAM, GPU, temperaturas, discos, rede e processos.",
                NexusPermission.HardwareMonitor,
                osSupported ? "Sensores de SO ativos; GPU/térmico sujeitos ao suporte do hardware" : "Coletores parciais fora do Windows"),
            IntegrationModuleKind.Media => (
                "nexus.media",
                "Nexus Media",
                "Controles nativos de mídia e áudio do Windows.",
                NexusPermission.MediaControl,
                osSupported ? "Áudio CoreAudio e comandos de mídia prontos" : "Disponível apenas no host Windows"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };

        var availability = !isEnabled
            ? ModuleAvailabilityState.Disabled
            : osSupported
                ? (kind == IntegrationModuleKind.Monitor ? ModuleAvailabilityState.Limited : ModuleAvailabilityState.Available)
                : ModuleAvailabilityState.Unavailable;

        return new IntegrationModuleStatusSnapshot
        {
            ModuleKind = kind,
            ModuleId = id,
            DisplayName = name,
            Description = desc,
            IsEnabled = isEnabled,
            RequiredPermission = perm,
            Availability = availability,
            StatusMessage = isEnabled ? availMsg : "Módulo desativado nas configurações locais.",
            RequireConfirmationForCriticalActions = confirmCritical,
            UpdatedAtUtc = updatedAtUtc
        };
    }

    private static IReadOnlyList<string> NormalizeAppList(IReadOnlyList<string>? apps)
    {
        if (apps is null || apps.Count == 0)
        {
            return Array.Empty<string>();
        }

        return apps
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
