using System.Collections.ObjectModel;
using Nexus.Contracts.Security;
using Nexus.Contracts.Transfers;
using Nexus.Core.Abstractions;
using Nexus.Core.Devices;
using Nexus.Core.Storage;
using Nexus.Core.Transfers;
using Nexus.Desktop.Services.Mvvm;

namespace Nexus.Desktop.Services.ViewModels;

public sealed class TransferViewModel : ObservableObject, IDisposable
{
    private readonly ITransferManager _transferManager;
    private readonly ITransferStorageProvider _storageProvider;
    private readonly ITrustedDeviceRepository _trustedDeviceRepository;
    private readonly ILocalDeviceContext _localDevice;

    private TrustedDeviceRecord? _selectedPeer;
    private TransferDuplicatePolicy _selectedDuplicatePolicy = TransferDuplicatePolicy.Rename;
    private int _maxConcurrentTransfers = 2;
    private bool _autoDownloadIncoming = true;
    private bool _autoResumeOnReconnect = true;
    private string _statusMessage = "Pronto para transferir arquivos por HTTP com verificação SHA-256.";
    private string _activeCountText = "0 ativas";
    private string _receivedCountText = "0 recebidos";
    private string _historyCountText = "0 no histórico";

    public TransferViewModel(
        ITransferManager transferManager,
        ITransferStorageProvider storageProvider,
        ITrustedDeviceRepository trustedDeviceRepository,
        ILocalDeviceContext localDevice,
        IUiDispatcher? uiDispatcher = null)
        : base(uiDispatcher)
    {
        _transferManager = transferManager;
        _storageProvider = storageProvider;
        _trustedDeviceRepository = trustedDeviceRepository;
        _localDevice = localDevice;

        DuplicatePolicies =
        [
            TransferDuplicatePolicy.Rename,
            TransferDuplicatePolicy.Overwrite,
            TransferDuplicatePolicy.Skip
        ];

        SendSelectedFilesCommand = new AsyncRelayCommand(SendSelectedFilesAsync, () => SelectedPeer is not null && PendingSelectedFiles.Count > 0, uiDispatcher: uiDispatcher);
        ClearPendingFilesCommand = new RelayCommand(ClearPendingFiles, uiDispatcher: uiDispatcher);
        RemovePendingFileCommand = new AsyncRelayCommand<string>((path, ct) =>
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return Task.CompletedTask;
            }

            return RunOnUiAsync(() =>
            {
                PendingSelectedFiles.Remove(path);
                SendSelectedFilesCommand.NotifyCanExecuteChanged();
            }, ct);
        }, uiDispatcher: uiDispatcher);

        PauseTransferCommand = new AsyncRelayCommand<TransferDisplayItem>((item, ct) =>
            item is null ? Task.CompletedTask : ExecuteControlAsync(() => _transferManager.PauseTransferAsync(item.TransferId, ct), ct), uiDispatcher: uiDispatcher);

        ResumeTransferCommand = new AsyncRelayCommand<TransferDisplayItem>((item, ct) =>
            item is null ? Task.CompletedTask : ExecuteControlAsync(() => _transferManager.ResumeTransferAsync(item.TransferId, ct), ct), uiDispatcher: uiDispatcher);

        CancelTransferCommand = new AsyncRelayCommand<TransferDisplayItem>((item, ct) =>
            item is null ? Task.CompletedTask : ExecuteControlAsync(() => _transferManager.CancelTransferAsync(item.TransferId, null, ct), ct), uiDispatcher: uiDispatcher);

        ResumeInterruptedCommand = new AsyncRelayCommand(ResumeInterruptedAsync, uiDispatcher: uiDispatcher);
        SyncIncomingCommand = new AsyncRelayCommand(SyncIncomingAsync, uiDispatcher: uiDispatcher);
        ApplyQueueConfigCommand = new AsyncRelayCommand(ApplyQueueConfigAsync, uiDispatcher: uiDispatcher);
        ClearHistoryCommand = new AsyncRelayCommand(ClearHistoryAsync, uiDispatcher: uiDispatcher);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, uiDispatcher: uiDispatcher);

        _transferManager.TransferUpdated += OnTransferUpdated;
    }

    public ObservableCollection<string> PendingSelectedFiles { get; } = [];
    public ObservableCollection<TrustedDeviceRecord> AuthorizedPeers { get; } = [];
    public ObservableCollection<TransferDisplayItem> ActiveTransfers { get; } = [];
    public ObservableCollection<TransferDisplayItem> ReceivedFiles { get; } = [];
    public ObservableCollection<TransferDisplayItem> HistoryItems { get; } = [];
    public IReadOnlyList<TransferDuplicatePolicy> DuplicatePolicies { get; }

    public string ReceivedFolderPath => _storageProvider.ReceivedDirectory;

    public TrustedDeviceRecord? SelectedPeer
    {
        get => _selectedPeer;
        set
        {
            if (SetProperty(ref _selectedPeer, value))
            {
                SendSelectedFilesCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public TransferDuplicatePolicy SelectedDuplicatePolicy
    {
        get => _selectedDuplicatePolicy;
        set => SetProperty(ref _selectedDuplicatePolicy, value);
    }

    public int MaxConcurrentTransfers
    {
        get => _maxConcurrentTransfers;
        set => SetProperty(ref _maxConcurrentTransfers, Math.Clamp(value, 1, 8));
    }

    public bool AutoDownloadIncoming
    {
        get => _autoDownloadIncoming;
        set => SetProperty(ref _autoDownloadIncoming, value);
    }

    public bool AutoResumeOnReconnect
    {
        get => _autoResumeOnReconnect;
        set => SetProperty(ref _autoResumeOnReconnect, value);
    }

    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public string ActiveCountText { get => _activeCountText; private set => SetProperty(ref _activeCountText, value); }
    public string ReceivedCountText { get => _receivedCountText; private set => SetProperty(ref _receivedCountText, value); }
    public string HistoryCountText { get => _historyCountText; private set => SetProperty(ref _historyCountText, value); }

    public AsyncRelayCommand SendSelectedFilesCommand { get; }
    public RelayCommand ClearPendingFilesCommand { get; }
    public AsyncRelayCommand<string> RemovePendingFileCommand { get; }
    public AsyncRelayCommand<TransferDisplayItem> PauseTransferCommand { get; }
    public AsyncRelayCommand<TransferDisplayItem> ResumeTransferCommand { get; }
    public AsyncRelayCommand<TransferDisplayItem> CancelTransferCommand { get; }
    public AsyncRelayCommand ResumeInterruptedCommand { get; }
    public AsyncRelayCommand SyncIncomingCommand { get; }
    public AsyncRelayCommand ApplyQueueConfigCommand { get; }
    public AsyncRelayCommand ClearHistoryCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _transferManager.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var opts = _transferManager.Options;

        await RunOnUiAsync(() =>
        {
            MaxConcurrentTransfers = opts.MaxConcurrentTransfers;
            SelectedDuplicatePolicy = opts.DefaultDuplicatePolicy;
            AutoDownloadIncoming = opts.AutoDownloadIncoming;
            AutoResumeOnReconnect = opts.AutoResumeOnReconnect;
        }, cancellationToken).ConfigureAwait(false);

        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    public void AddSelectedFiles(IEnumerable<string> filePaths)
    {
        ArgumentNullException.ThrowIfNull(filePaths);
        var normalized = filePaths
            .Where(raw => !string.IsNullOrWhiteSpace(raw))
            .Select(Path.GetFullPath)
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        RunOnUi(() =>
        {
            var added = 0;
            foreach (var full in normalized)
            {
                if (!PendingSelectedFiles.Contains(full, StringComparer.OrdinalIgnoreCase))
                {
                    PendingSelectedFiles.Add(full);
                    added++;
                }
            }

            if (added > 0)
            {
                StatusMessage = $"{PendingSelectedFiles.Count} arquivo(s) selecionado(s) pronto(s) para envio.";
            }

            SendSelectedFilesCommand.NotifyCanExecuteChanged();
        });
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var peersResult = await _trustedDeviceRepository.ListAuthorizedAsync(cancellationToken).ConfigureAwait(false);
        await _transferManager.GetAllTransfersAsync(cancellationToken).ConfigureAwait(false);

        var peers = peersResult.IsSuccess
            ? peersResult.Value
                .Where(d => d.DeviceId != _localDevice.DeviceId)
                .OrderBy(d => d.CustomName, StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : null;
        var projection = BuildCollectionSnapshot();

        await RunOnUiAsync(() =>
        {
            if (peers is not null)
            {
                var prevId = SelectedPeer?.DeviceId;
                ReplaceCollection(AuthorizedPeers, peers);
                SelectedPeer = peers.FirstOrDefault(p => p.DeviceId == prevId) ?? peers.FirstOrDefault();
            }

            ApplyCollectionSnapshot(projection);
            SendSelectedFilesCommand.NotifyCanExecuteChanged();
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendSelectedFilesAsync(CancellationToken cancellationToken)
    {
        TrustedDeviceRecord? peer = null;
        TransferDuplicatePolicy policy = TransferDuplicatePolicy.Rename;
        string[] batch = Array.Empty<string>();

        await RunOnUiAsync(() =>
        {
            peer = SelectedPeer;
            policy = SelectedDuplicatePolicy;
            batch = PendingSelectedFiles.ToArray();
        }, cancellationToken).ConfigureAwait(false);

        if (peer is null)
        {
            await RunOnUiAsync(() => StatusMessage = "Selecione um dispositivo autorizado para enviar arquivos.", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (batch.Length == 0)
        {
            await RunOnUiAsync(() => StatusMessage = "Adicione ao menos um arquivo antes de iniciar o envio.", cancellationToken).ConfigureAwait(false);
            return;
        }

        var result = await _transferManager.EnqueueSendFilesAsync(
            peer.DeviceId,
            peer.CustomName,
            batch,
            policy,
            cancellationToken).ConfigureAwait(false);

        var projection = BuildCollectionSnapshot();
        await RunOnUiAsync(() =>
        {
            if (result.IsFailure)
            {
                StatusMessage = $"Erro ao enfileirar envio: {result.Error!.Message}";
                return;
            }

            PendingSelectedFiles.Clear();
            SendSelectedFilesCommand.NotifyCanExecuteChanged();
            ApplyCollectionSnapshot(projection);
            StatusMessage = $"{result.Value.Count} arquivo(s) adicionado(s) à fila de envio para {peer.CustomName}.";
        }, cancellationToken).ConfigureAwait(false);
    }

    private void ClearPendingFiles()
    {
        RunOnUi(() =>
        {
            PendingSelectedFiles.Clear();
            SendSelectedFilesCommand.NotifyCanExecuteChanged();
            StatusMessage = "Seleção de envio limpa.";
        });
    }

    private async Task ExecuteControlAsync(Func<Task<Core.Errors.Result<FileTransferRecord>>> action, CancellationToken cancellationToken)
    {
        var result = await action().ConfigureAwait(false);
        var projection = BuildCollectionSnapshot();
        var msg = result.IsSuccess
            ? $"Transferência '{result.Value.SafeFileName}': {TransferDisplayItem.FromRecord(result.Value).StageDisplay}."
            : result.Error!.Message;

        await RunOnUiAsync(() =>
        {
            ApplyCollectionSnapshot(projection);
            StatusMessage = msg;
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task ResumeInterruptedAsync(CancellationToken cancellationToken)
    {
        var result = await _transferManager.ResumeInterruptedTransfersAsync(cancellationToken).ConfigureAwait(false);
        var projection = BuildCollectionSnapshot();
        var msg = result.IsSuccess
            ? $"{result.Value} transferência(s) interrompida(s) recolocada(s) na fila."
            : result.Error!.Message;

        await RunOnUiAsync(() =>
        {
            ApplyCollectionSnapshot(projection);
            StatusMessage = msg;
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task SyncIncomingAsync(CancellationToken cancellationToken)
    {
        var result = await _transferManager.SyncIncomingTransfersAsync(cancellationToken).ConfigureAwait(false);
        var projection = BuildCollectionSnapshot();
        var msg = result.IsSuccess
            ? $"{result.Value} transferência(s) recebida(s) sincronizada(s) do servidor."
            : result.Error!.Message;

        await RunOnUiAsync(() =>
        {
            ApplyCollectionSnapshot(projection);
            StatusMessage = msg;
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task ApplyQueueConfigAsync(CancellationToken cancellationToken)
    {
        TransferQueueOptions next = _transferManager.Options;
        await RunOnUiAsync(() =>
        {
            next = _transferManager.Options with
            {
                MaxConcurrentTransfers = MaxConcurrentTransfers,
                DefaultDuplicatePolicy = SelectedDuplicatePolicy,
                AutoDownloadIncoming = AutoDownloadIncoming,
                AutoResumeOnReconnect = AutoResumeOnReconnect
            };
        }, cancellationToken).ConfigureAwait(false);

        var result = await _transferManager.UpdateConfigurationAsync(next, cancellationToken).ConfigureAwait(false);
        var msg = result.IsSuccess
            ? $"Configuração salva: até {next.MaxConcurrentTransfers} transferência(s) simultânea(s), duplicados = {next.DefaultDuplicatePolicy}."
            : result.Error!.Message;

        await RunOnUiAsync(() => StatusMessage = msg, cancellationToken).ConfigureAwait(false);
    }

    private async Task ClearHistoryAsync(CancellationToken cancellationToken)
    {
        var result = await _transferManager.ClearHistoryAsync(cancellationToken).ConfigureAwait(false);
        var projection = BuildCollectionSnapshot();
        var msg = result.IsSuccess
            ? $"{result.Value} registro(s) concluído(s)/encerrado(s) removido(s) do histórico."
            : result.Error!.Message;

        await RunOnUiAsync(() =>
        {
            ApplyCollectionSnapshot(projection);
            StatusMessage = msg;
        }, cancellationToken).ConfigureAwait(false);
    }

    private void OnTransferUpdated(object? sender, FileTransferRecord _)
    {
        var projection = BuildCollectionSnapshot();
        RunOnUi(() => ApplyCollectionSnapshot(projection));
    }

    private sealed record TransferCollectionSnapshot(
        TransferDisplayItem[] Active,
        TransferDisplayItem[] Received,
        TransferDisplayItem[] History);

    private TransferCollectionSnapshot BuildCollectionSnapshot()
    {
        var all = _transferManager.History;
        var active = all
            .Where(r => r.State is TransferState.Pending or TransferState.InProgress or TransferState.Paused)
            .Select(TransferDisplayItem.FromRecord)
            .ToArray();

        var received = all
            .Where(r => r.Direction == TransferDirection.Download && r.State == TransferState.Completed)
            .Select(TransferDisplayItem.FromRecord)
            .ToArray();

        var history = all
            .Select(TransferDisplayItem.FromRecord)
            .ToArray();

        return new TransferCollectionSnapshot(active, received, history);
    }

    private void ApplyCollectionSnapshot(TransferCollectionSnapshot snapshot)
    {
        ReplaceCollection(ActiveTransfers, snapshot.Active);
        ReplaceCollection(ReceivedFiles, snapshot.Received);
        ReplaceCollection(HistoryItems, snapshot.History);

        ActiveCountText = $"{snapshot.Active.Length} em andamento";
        ReceivedCountText = $"{snapshot.Received.Length} recebido(s)";
        HistoryCountText = $"{snapshot.History.Length} no histórico";
    }

    public void Dispose()
    {
        _transferManager.TransferUpdated -= OnTransferUpdated;
    }
}
