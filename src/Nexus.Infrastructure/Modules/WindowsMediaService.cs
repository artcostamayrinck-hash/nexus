using Microsoft.Extensions.Logging;
using Nexus.Contracts.Devices;
using Nexus.Contracts.Modules;
using Nexus.Core.Abstractions;
using Nexus.Core.Errors;
using Nexus.Core.Modules;

namespace Nexus.Infrastructure.Modules;

/// <summary>
/// Provides native Windows audio volume/mute state and media playback controls
/// via Windows Core Audio COM and Win32 SendInput. Reports metadata availability honestly.
/// </summary>
public sealed class WindowsMediaService : IWindowsMediaService
{
    private readonly IModuleSettingsService _settingsService;
    private readonly IWindowsControlExecutor _controlExecutor;
    private readonly ILocalDeviceContext _hostIdentity;
    private readonly IClock _clock;
    private readonly ILogger<WindowsMediaService> _logger;
    private MediaPlaybackStatus _lastCommandHintStatus = MediaPlaybackStatus.Unknown;

    public WindowsMediaService(
        IModuleSettingsService settingsService,
        IWindowsControlExecutor controlExecutor,
        ILocalDeviceContext hostIdentity,
        IClock clock,
        ILogger<WindowsMediaService> logger)
    {
        _settingsService = settingsService;
        _controlExecutor = controlExecutor;
        _hostIdentity = hostIdentity;
        _clock = clock;
        _logger = logger;
    }

    public bool IsSupportedOnCurrentOs => _controlExecutor.IsSupportedOnCurrentOs;

    public async Task<Result<MediaPlaybackStateContract>> GetCurrentStateAsync(CancellationToken cancellationToken = default)
    {
        var settingsRes = await _settingsService.GetMediaSettingsAsync(cancellationToken).ConfigureAwait(false);
        if (settingsRes.IsFailure)
        {
            return Result<MediaPlaybackStateContract>.Failure(settingsRes.Error!);
        }

        if (!settingsRes.Value.IsEnabled)
        {
            return Result<MediaPlaybackStateContract>.Failure(
                NexusError.Forbidden("Nexus Media está desativado nas configurações locais.", "MODULE_DISABLED"));
        }

        var (volReading, muteReading) = WindowsCoreAudioInterop.ReadAudioState();
        var note = OperatingSystem.IsWindows()
            ? "Metadados de faixa (SMTC WinRT) indisponíveis sem sessão UWP/WinRT projetada nesta build; controles nativos de áudio e transporte estão ativos."
            : "Controles nativos de mídia Windows indisponíveis fora do Windows.";

        return Result<MediaPlaybackStateContract>.Success(new MediaPlaybackStateContract
        {
            HostDeviceId = _hostIdentity.DeviceId,
            IsAvailable = IsSupportedOnCurrentOs,
            IsMetadataAvailable = false,
            MetadataAvailabilityNote = note,
            PlaybackStatus = _lastCommandHintStatus,
            VolumePercent = volReading,
            IsMuted = muteReading,
            UpdatedAtUtc = _clock.UtcNow
        });
    }

    public async Task<Result<MediaPlaybackStateContract>> ExecuteCommandAsync(
        MediaControlCommandRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var errors = request.Validate().ToArray();
        if (errors.Length > 0)
        {
            return Result<MediaPlaybackStateContract>.Failure(
                NexusError.Validation(errors[0].Message, code: errors[0].Code));
        }

        var settingsRes = await _settingsService.GetMediaSettingsAsync(cancellationToken).ConfigureAwait(false);
        if (settingsRes.IsFailure)
        {
            return Result<MediaPlaybackStateContract>.Failure(settingsRes.Error!);
        }

        if (!settingsRes.Value.IsEnabled)
        {
            return Result<MediaPlaybackStateContract>.Failure(
                NexusError.Forbidden("Nexus Media está desativado nas configurações locais.", "MODULE_DISABLED"));
        }

        var mappedAction = request.Action switch
        {
            MediaControlAction.PlayPause => RemoteControlActionType.MediaPlayPause,
            MediaControlAction.Play => RemoteControlActionType.MediaPlayPause,
            MediaControlAction.Pause => RemoteControlActionType.MediaPlayPause,
            MediaControlAction.Stop => RemoteControlActionType.MediaStop,
            MediaControlAction.Next => RemoteControlActionType.MediaNext,
            MediaControlAction.Previous => RemoteControlActionType.MediaPrevious,
            MediaControlAction.VolumeUp => RemoteControlActionType.VolumeUp,
            MediaControlAction.VolumeDown => RemoteControlActionType.VolumeDown,
            MediaControlAction.SetVolume => RemoteControlActionType.VolumeSet,
            MediaControlAction.ToggleMute => RemoteControlActionType.VolumeMuteToggle,
            _ => throw new ArgumentOutOfRangeException(nameof(request.Action), request.Action, null)
        };

        var controlReq = new RemoteControlCommandRequest
        {
            CommandId = request.CommandId,
            SourceDeviceId = request.SourceDeviceId,
            TargetDeviceId = request.TargetDeviceId,
            Action = mappedAction,
            VolumePercent = request.VolumePercent,
            RequestedAtUtc = request.RequestedAtUtc
        };

        var exec = await _controlExecutor.ExecuteAsync(controlReq, cancellationToken).ConfigureAwait(false);
        if (exec.IsFailure)
        {
            return Result<MediaPlaybackStateContract>.Failure(exec.Error!);
        }

        if (!exec.Value.Executed)
        {
            return Result<MediaPlaybackStateContract>.Failure(
                NexusError.Unexpected(exec.Value.Message, exec.Value.ErrorCode ?? "MEDIA_EXECUTION_FAILED"));
        }

        _lastCommandHintStatus = request.Action switch
        {
            MediaControlAction.Play => MediaPlaybackStatus.Playing,
            MediaControlAction.Pause => MediaPlaybackStatus.Paused,
            MediaControlAction.Stop => MediaPlaybackStatus.Stopped,
            MediaControlAction.PlayPause => _lastCommandHintStatus == MediaPlaybackStatus.Playing
                ? MediaPlaybackStatus.Paused
                : MediaPlaybackStatus.Playing,
            _ => _lastCommandHintStatus
        };

        _logger.LogInformation("Executed media control command {Action} on host {HostId}", request.Action, _hostIdentity.DeviceId);
        return await GetCurrentStateAsync(cancellationToken).ConfigureAwait(false);
    }
}
