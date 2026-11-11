using Nexus.Contracts.Pairing;
using Nexus.Contracts.Security;
using Nexus.Desktop.Services.Mvvm;
using Nexus.Desktop.Services.Security;
using Nexus.Infrastructure.Security;
using Nexus.Security.Identity;

namespace Nexus.Desktop.Services.ViewModels;

public sealed class PairingViewModel : ObservableObject, IDisposable
{
    private readonly ICryptographicIdentityProvider _identityProvider;
    private readonly IDesktopPairingClient _pairingClient;
    private readonly DevicesViewModel _devicesViewModel;

    private CancellationTokenSource? _pollCts;

    private string _localDeviceName = string.Empty;
    private string _localDeviceIdText = string.Empty;
    private string _localFingerprintDisplay = "-";
    private string _registeredAtText = "-";

    private Guid? _activePairingId;
    private bool _isRemoteMode;
    private string _endpointUsed = string.Empty;
    private string? _tlsThumbprint;
    private DateTimeOffset _expiresAtUtc;

    private PairingSessionState _currentState = PairingSessionState.Idle;
    private string _stateLabel = NexusPairingProtocolClient.GetPairingStateLabel(PairingSessionState.Idle);
    private string _statusMessage = "Gere um QR Code temporário para parear um dispositivo Android.";
    private string _modeBadge = "Local";
    private byte[]? _qrPngBytes;
    private string _qrPayloadJson = string.Empty;
    private string _countdownText = "-";

    private string _candidateDeviceName = "-";
    private string _candidateDetail = "-";
    private string _candidateFingerprintDisplay = "-";
    private string _verificationCode = "--- ---";

    private bool _allowViewStatus = true;
    private bool _allowNotifications = true;
    private bool _allowClipboard = true;
    private bool _allowFileTransfer = true;
    private bool _allowRemoteCommands;
    private bool _allowManageDevices;

    public PairingViewModel(
        ICryptographicIdentityProvider identityProvider,
        IDesktopPairingClient pairingClient,
        DevicesViewModel devicesViewModel)
    {
        _identityProvider = identityProvider;
        _pairingClient = pairingClient;
        _devicesViewModel = devicesViewModel;

        LoadIdentityCommand = new AsyncRelayCommand(LoadIdentityAsync);
        GenerateQrCommand = new AsyncRelayCommand(GenerateQrAsync);
        PollStatusCommand = new AsyncRelayCommand(PollStatusOnceAsync, () => _activePairingId.HasValue);
        ApprovePairingCommand = new AsyncRelayCommand(ApproveAsync, () => CurrentState == PairingSessionState.AwaitingConfirmation);
        RejectPairingCommand = new AsyncRelayCommand(RejectAsync, () => CurrentState == PairingSessionState.AwaitingConfirmation);
        ResetPairingCommand = new AsyncRelayCommand(ResetAsync);
    }

    public AsyncRelayCommand LoadIdentityCommand { get; }
    public AsyncRelayCommand GenerateQrCommand { get; }
    public AsyncRelayCommand PollStatusCommand { get; }
    public AsyncRelayCommand ApprovePairingCommand { get; }
    public AsyncRelayCommand RejectPairingCommand { get; }
    public AsyncRelayCommand ResetPairingCommand { get; }

    public string LocalDeviceName { get => _localDeviceName; private set => SetProperty(ref _localDeviceName, value); }
    public string LocalDeviceIdText { get => _localDeviceIdText; private set => SetProperty(ref _localDeviceIdText, value); }
    public string LocalFingerprintDisplay { get => _localFingerprintDisplay; private set => SetProperty(ref _localFingerprintDisplay, value); }
    public string RegisteredAtText { get => _registeredAtText; private set => SetProperty(ref _registeredAtText, value); }

    public PairingSessionState CurrentState
    {
        get => _currentState;
        private set
        {
            if (SetProperty(ref _currentState, value))
            {
                StateLabel = NexusPairingProtocolClient.GetPairingStateLabel(value);
                OnPropertyChanged(nameof(HasQrCode));
                OnPropertyChanged(nameof(IsAwaitingConfirmation));
                ApprovePairingCommand.NotifyCanExecuteChanged();
                RejectPairingCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string StateLabel { get => _stateLabel; private set => SetProperty(ref _stateLabel, value); }
    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public string ModeBadge { get => _modeBadge; private set => SetProperty(ref _modeBadge, value); }

    public byte[]? QrPngBytes
    {
        get => _qrPngBytes;
        private set
        {
            if (SetProperty(ref _qrPngBytes, value))
            {
                OnPropertyChanged(nameof(HasQrCode));
            }
        }
    }

    public string QrPayloadJson { get => _qrPayloadJson; private set => SetProperty(ref _qrPayloadJson, value); }
    public string CountdownText { get => _countdownText; private set => SetProperty(ref _countdownText, value); }

    public string CandidateDeviceName { get => _candidateDeviceName; private set => SetProperty(ref _candidateDeviceName, value); }
    public string CandidateDetail { get => _candidateDetail; private set => SetProperty(ref _candidateDetail, value); }
    public string CandidateFingerprintDisplay { get => _candidateFingerprintDisplay; private set => SetProperty(ref _candidateFingerprintDisplay, value); }
    public string VerificationCode { get => _verificationCode; private set => SetProperty(ref _verificationCode, value); }

    public bool AllowViewStatus { get => _allowViewStatus; set => SetProperty(ref _allowViewStatus, value); }
    public bool AllowNotifications { get => _allowNotifications; set => SetProperty(ref _allowNotifications, value); }
    public bool AllowClipboard { get => _allowClipboard; set => SetProperty(ref _allowClipboard, value); }
    public bool AllowFileTransfer { get => _allowFileTransfer; set => SetProperty(ref _allowFileTransfer, value); }
    public bool AllowRemoteCommands { get => _allowRemoteCommands; set => SetProperty(ref _allowRemoteCommands, value); }
    public bool AllowManageDevices { get => _allowManageDevices; set => SetProperty(ref _allowManageDevices, value); }

    public bool HasQrCode => QrPngBytes is { Length: > 0 } && CurrentState == PairingSessionState.AwaitingScan;
    public bool IsAwaitingConfirmation => CurrentState == PairingSessionState.AwaitingConfirmation;

    public async Task LoadIdentityAsync(CancellationToken cancellationToken = default)
    {
        var identity = await _identityProvider.GetOrCreateIdentityAsync(cancellationToken).ConfigureAwait(false);
        if (identity.IsSuccess)
        {
            LocalDeviceName = identity.Value.CustomName;
            LocalDeviceIdText = identity.Value.DeviceId.ToString("D");
            LocalFingerprintDisplay = NexusPairingProtocolClient.FormatFingerprint(identity.Value.PublicKeyFingerprint);
            RegisteredAtText = $"{identity.Value.RegisteredAtUtc.ToLocalTime():dd/MM/yyyy HH:mm} · v{identity.Value.AppVersion}";
        }
    }

    public async Task GenerateQrAsync(CancellationToken cancellationToken = default)
    {
        CancelPolling();
        await LoadIdentityAsync(cancellationToken);

        CurrentState = PairingSessionState.Validating;
        StatusMessage = "Gerando QR Code criptográfico temporário...";

        var started = await _pairingClient.StartPairingAsync(120, cancellationToken);
        if (started.IsFailure)
        {
            CurrentState = PairingSessionState.Failed;
            StatusMessage = started.Error!.Message;
            QrPngBytes = null;
            QrPayloadJson = string.Empty;
            return;
        }

        _activePairingId = started.Value.Snapshot.PairingId;
        _isRemoteMode = started.Value.IsRemoteServer;
        _endpointUsed = started.Value.EndpointUsed;
        _tlsThumbprint = started.Value.TlsThumbprint;
        _expiresAtUtc = started.Value.Snapshot.ExpiresAtUtc;

        ModeBadge = _isRemoteMode ? $"Servidor HTTPS/API ({_endpointUsed})" : "Modo Local SQLite";
        QrPngBytes = started.Value.QrPngBytes;
        QrPayloadJson = started.Value.Snapshot.QrPayloadJson ?? string.Empty;
        ResetCandidateInfo();

        ApplySnapshot(started.Value.Snapshot);
        PollStatusCommand.NotifyCanExecuteChanged();
        UpdateCountdown();
        StatusMessage = "Escaneie o QR Code no aplicativo NEXUS Android dentro de 2 minutos.";

        StartBackgroundPolling();
    }

    public async Task PollStatusOnceAsync(CancellationToken cancellationToken = default)
    {
        if (!_activePairingId.HasValue)
        {
            return;
        }

        UpdateCountdown();

        var result = await _pairingClient.GetSessionAsync(
            _activePairingId.Value,
            _isRemoteMode,
            _endpointUsed,
            _tlsThumbprint,
            cancellationToken);

        if (result.IsFailure)
        {
            StatusMessage = result.Error!.Message;
            return;
        }

        ApplySnapshot(result.Value);
    }

    public async Task ApproveAsync(CancellationToken cancellationToken = default)
    {
        await ConfirmDecisionAsync(approve: true, rejectionReason: null, cancellationToken);
    }

    public async Task RejectAsync(CancellationToken cancellationToken = default)
    {
        await ConfirmDecisionAsync(approve: false, rejectionReason: "Recusado manualmente no Windows", cancellationToken);
    }

    private async Task ConfirmDecisionAsync(bool approve, string? rejectionReason, CancellationToken cancellationToken)
    {
        if (!_activePairingId.HasValue)
        {
            return;
        }

        CancelPolling();
        CurrentState = PairingSessionState.Validating;
        StatusMessage = approve ? "Confirmando estabelecimento de confiança..." : "Rejeitando solicitação de pareamento...";

        var result = await _pairingClient.ConfirmPairingAsync(
            _activePairingId.Value,
            approve,
            BuildGrantedPermissions(),
            _isRemoteMode,
            _endpointUsed,
            _tlsThumbprint,
            rejectionReason,
            cancellationToken);

        if (result.IsFailure)
        {
            CurrentState = PairingSessionState.Failed;
            StatusMessage = result.Error!.Message;
            return;
        }

        ApplySnapshot(result.Value);
        await _devicesViewModel.LoadTrustedDevicesAsync(cancellationToken);
        await _devicesViewModel.LoadDevicesAsync(cancellationToken);
    }

    private Task ResetAsync(CancellationToken cancellationToken)
    {
        CancelPolling();
        _activePairingId = null;
        QrPngBytes = null;
        QrPayloadJson = string.Empty;
        CountdownText = "-";
        ResetCandidateInfo();
        CurrentState = PairingSessionState.Idle;
        StatusMessage = "Pronto para iniciar novo pareamento seguro via QR Code.";
        PollStatusCommand.NotifyCanExecuteChanged();
        return Task.CompletedTask;
    }

    private NexusPermission BuildGrantedPermissions()
    {
        var perms = NexusPermission.None;
        if (AllowViewStatus) perms |= NexusPermission.ViewStatus;
        if (AllowNotifications) perms |= NexusPermission.Notifications;
        if (AllowClipboard) perms |= NexusPermission.Clipboard;
        if (AllowFileTransfer) perms |= NexusPermission.FileTransfer;
        if (AllowRemoteCommands) perms |= NexusPermission.RemoteCommands;
        if (AllowManageDevices) perms |= NexusPermission.ManageDevices;
        return perms == NexusPermission.None ? NexusPermission.ViewStatus : perms;
    }

    private void ApplySnapshot(PairingSessionSnapshot snapshot)
    {
        _expiresAtUtc = snapshot.ExpiresAtUtc;
        CurrentState = snapshot.State;

        if (!string.IsNullOrWhiteSpace(snapshot.CandidateDeviceName))
        {
            CandidateDeviceName = snapshot.CandidateDeviceName;
            CandidateDetail = $"{snapshot.CandidatePlatform} · {snapshot.CandidateDeviceType} · App {snapshot.CandidateAppVersion}";
            CandidateFingerprintDisplay = NexusPairingProtocolClient.FormatFingerprint(snapshot.CandidatePublicKeyFingerprint);
            VerificationCode = FormatVerificationCode(snapshot.VerificationCode);
        }

        switch (snapshot.State)
        {
            case PairingSessionState.AwaitingConfirmation:
                CancelPolling();
                QrPngBytes = null;
                StatusMessage = $"Confirme que o código {VerificationCode} corresponde ao exibido no Android.";
                break;

            case PairingSessionState.Connected:
                CancelPolling();
                QrPngBytes = null;
                CountdownText = "Concluído";
                StatusMessage = $"Dispositivo '{CandidateDeviceName}' pareado e autenticado com sucesso!";
                break;

            case PairingSessionState.Expired:
                CancelPolling();
                QrPngBytes = null;
                CountdownText = "Expirado";
                StatusMessage = snapshot.FailureReason ?? "QR Code expirado. Gere um novo QR Code.";
                break;

            case PairingSessionState.Rejected:
            case PairingSessionState.Failed:
                CancelPolling();
                QrPngBytes = null;
                CountdownText = "Encerrado";
                StatusMessage = snapshot.FailureReason ?? "Pareamento não concluído.";
                break;
        }
    }

    private void UpdateCountdown()
    {
        if (CurrentState != PairingSessionState.AwaitingScan)
        {
            return;
        }

        var remaining = (int)Math.Ceiling((_expiresAtUtc - DateTimeOffset.UtcNow).TotalSeconds);
        CountdownText = remaining > 0 ? $"Expira em {remaining}s" : "Expirado";
    }

    private void ResetCandidateInfo()
    {
        CandidateDeviceName = "-";
        CandidateDetail = "Aguardando leitura do QR Code pelo Android";
        CandidateFingerprintDisplay = "-";
        VerificationCode = "--- ---";
    }

    private static string FormatVerificationCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length != 6)
        {
            return code ?? "--- ---";
        }

        return $"{code[..3]} {code[3..]}";
    }

    private void StartBackgroundPolling()
    {
        _pollCts = new CancellationTokenSource();
        var token = _pollCts.Token;
        _ = RunPollLoopAsync(token);
    }

    private async Task RunPollLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested && CurrentState == PairingSessionState.AwaitingScan)
            {
                UpdateCountdown();
                await Task.Delay(TimeSpan.FromSeconds(1.2), token);
                if (!token.IsCancellationRequested && _activePairingId.HasValue)
                {
                    await PollStatusOnceAsync(token);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when pairing advances or resets
        }
    }

    private void CancelPolling()
    {
        if (_pollCts is null)
        {
            return;
        }

        _pollCts.Cancel();
        _pollCts.Dispose();
        _pollCts = null;
    }

    public void Dispose()
    {
        CancelPolling();
    }
}

