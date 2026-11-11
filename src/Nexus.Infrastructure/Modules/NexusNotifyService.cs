using Microsoft.Extensions.Logging;
using Nexus.Contracts.Modules;
using Nexus.Core.Abstractions;
using Nexus.Core.Errors;
using Nexus.Core.Modules;

namespace Nexus.Infrastructure.Modules;

/// <summary>
/// Manages Android-to-Windows notification forwarding, per-app allowlist/blocklist filtering,
/// independent module disable toggle, and local notification history.
/// </summary>
public sealed class NexusNotifyService : INexusNotifyService
{
    private readonly IModuleSettingsService _settingsService;
    private readonly IMobileNotificationFeedRepository _repository;
    private readonly IClock _clock;
    private readonly ILogger<NexusNotifyService> _logger;

    public NexusNotifyService(
        IModuleSettingsService settingsService,
        IMobileNotificationFeedRepository repository,
        IClock clock,
        ILogger<NexusNotifyService> logger)
    {
        _settingsService = settingsService;
        _repository = repository;
        _clock = clock;
        _logger = logger;
    }

    public event EventHandler<MobileNotificationRecord>? NotificationReceived;

    public async Task<Result<PushMobileNotificationReceipt>> IngestAsync(
        PushMobileNotificationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var errors = request.Validate().ToArray();
        if (errors.Length > 0)
        {
            return Result<PushMobileNotificationReceipt>.Failure(
                NexusError.Validation(errors[0].Message, code: errors[0].Code));
        }

        var settingsRes = await _settingsService.GetNotifySettingsAsync(cancellationToken).ConfigureAwait(false);
        if (settingsRes.IsFailure)
        {
            return Result<PushMobileNotificationReceipt>.Failure(settingsRes.Error!);
        }

        var settings = settingsRes.Value;
        var now = _clock.UtcNow;

        if (!settings.IsEnabled)
        {
            _logger.LogInformation("Notification {NotificationId} from '{App}' skipped because Nexus Notify is disabled.", request.NotificationId, request.AppName);
            return Result<PushMobileNotificationReceipt>.Success(new PushMobileNotificationReceipt
            {
                NotificationId = request.NotificationId,
                Accepted = true,
                Forwarded = false,
                FilterOutcome = "ModuleDisabled",
                Reason = "Encaminhamento desativado nas configurações do Nexus Notify.",
                ReceivedAtUtc = now
            });
        }

        var (allowed, filterOutcome, filterReason) = EvaluateAppFilter(request, settings);
        if (!allowed)
        {
            _logger.LogInformation("Notification {NotificationId} from '{App}' filtered ({Outcome}).", request.NotificationId, request.AppName, filterOutcome);
            return Result<PushMobileNotificationReceipt>.Success(new PushMobileNotificationReceipt
            {
                NotificationId = request.NotificationId,
                Accepted = true,
                Forwarded = false,
                FilterOutcome = filterOutcome,
                Reason = filterReason,
                ReceivedAtUtc = now
            });
        }

        var record = new MobileNotificationRecord
        {
            NotificationId = request.NotificationId,
            SourceDeviceId = request.SourceDeviceId,
            TargetDeviceId = request.TargetDeviceId,
            AppName = request.AppName.Trim(),
            PackageName = string.IsNullOrWhiteSpace(request.PackageName) ? null : request.PackageName.Trim(),
            Title = request.Title.Trim(),
            Body = request.Body ?? string.Empty,
            Urgency = request.Urgency,
            IsDismissed = false,
            ForwardedToDesktop = true,
            FilterOutcome = filterOutcome,
            CreatedAtUtc = request.CreatedAtUtc,
            ReceivedAtUtc = now
        };

        var saved = await _repository.SaveAsync(record, cancellationToken).ConfigureAwait(false);
        if (saved.IsFailure)
        {
            return Result<PushMobileNotificationReceipt>.Failure(saved.Error!);
        }

        NotificationReceived?.Invoke(this, record);

        return Result<PushMobileNotificationReceipt>.Success(new PushMobileNotificationReceipt
        {
            NotificationId = request.NotificationId,
            Accepted = true,
            Forwarded = true,
            FilterOutcome = filterOutcome,
            Reason = filterReason,
            ReceivedAtUtc = now
        });
    }

    public Task<Result<IReadOnlyList<MobileNotificationRecord>>> ListHistoryAsync(
        int limit = 100,
        CancellationToken cancellationToken = default)
        => _repository.ListRecentAsync(limit, cancellationToken);

    public Task<Result> DismissAsync(Guid notificationId, CancellationToken cancellationToken = default)
        => _repository.MarkDismissedAsync(notificationId, cancellationToken);

    public Task<Result<int>> ClearHistoryAsync(CancellationToken cancellationToken = default)
        => _repository.ClearAsync(cancellationToken);

    private static (bool Allowed, string Outcome, string Reason) EvaluateAppFilter(
        PushMobileNotificationRequest request,
        NotifyModuleSettings settings)
    {
        var matchesApp = (IReadOnlyList<string> candidates) => candidates.Any(c =>
            string.Equals(c, request.AppName, StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrWhiteSpace(request.PackageName) &&
             string.Equals(c, request.PackageName, StringComparison.OrdinalIgnoreCase)));

        return settings.FilterMode switch
        {
            NotificationFilterMode.AllowList => matchesApp(settings.AllowedApps)
                ? (true, "ForwardedAllowList", $"Aplicativo '{request.AppName}' autorizado na lista de permissões.")
                : (false, "BlockedNotInAllowList", $"Aplicativo '{request.AppName}' não está na lista de aplicativos permitidos."),

            NotificationFilterMode.BlockList => matchesApp(settings.BlockedApps)
                ? (false, "BlockedByDenylist", $"Aplicativo '{request.AppName}' está bloqueado pelo filtro.")
                : (true, "Forwarded", $"Aplicativo '{request.AppName}' permitido."),

            _ => (true, "Forwarded", $"Notificação de '{request.AppName}' encaminhada.")
        };
    }
}

