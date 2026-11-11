using System.Collections.ObjectModel;
using Nexus.Contracts.Security;
using Nexus.Contracts.Transfers;
using Nexus.Core.Abstractions;
using Nexus.Core.Devices;
using Nexus.Core.Storage;
using Nexus.Core.Transfers;
using Nexus.Desktop.Services.Mvvm;

namespace Nexus.Desktop.Services.ViewModels;

public sealed class TransferViewModel : ObservableObject
{
    private readonly ITransferManager _transferManager;
    private readonly ITransferStorageProvider _storageProvider;
    private readonly ITrustedDeviceRepository _trustedDeviceRepository;
    private readonly ILocalDeviceContext _localDevice;
    private readonly SynchronizationContext? _uiContext;

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
        ILocalDeviceContext localDevice)
    {
        _transferManager = transferManager;
        _storageProvider = storageProvider;
        _trustedDeviceRepository = trustedDeviceRepository;
        _localDevice = localDevice;
        _uiContext = SynchronizationContext.Current;

        DuplicatePolicies =
        [
            TransferDuplicatePolicy.Rename,
            TransferDuplicatePolicy.Overwrite,
            TransferDuplicatePolicy.Skip
        ];

        SendSelectedFilesCommand = new AsyncRelayCommand(SendSelectedFilesAsync, () => SelectedPeer is not null && PendingSelectedFiles.Count > 0);
        ClearPendingFilesCommand = new RelayCommand(ClearPendingFiles);
        RemovePendingFileCommand = new AsyncRelayCommand<string>((path, _) =>
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                RunOnUi(() =>
                {
                    PendingSelectedFiles.Remove(path);
                    SendSelectedFilesCommand.NotifyCanExecuteChanged();
                });
            }

            return Task.CompletedTask;
        });

        PauseTransferCommand = new AsyncRelayCommand<TransferDisplayItem>((item, ct) =>
            item is null ? Task.CompletedTask : ExecuteControlAsync(() => _transferManager.PauseTransferAsync(item.TransferId, ct)));

        ResumeTransferCommand = new AsyncRelayCommand<TransferDisplayItem>((item, ct) =>
            item is null ? Task.CompletedTask : ExecuteControlAsync(() => _transferManager.ResumeTransferAsync(item.TransferId, ct)));

        CancelTransferCommand = new AsyncRelayCommand<TransferDisplayItem>((item, ct) =>
            item is null ? Task.CompletedTask : ExecuteControlAsync(() => _transferManager.CancelTransferAsync(item.TransferId, null, ct)));

        ResumeInterruptedCommand = new AsyncRelayCommand(ResumeInterruptedAsync);
        SyncIncomingCommand = new AsyncRelayCommand(SyncIncomingAsync);
        ApplyQueueConfigCommand = new AsyncRelayCommand(ApplyQueueConfigAsync);
        ClearHistoryCommand = new AsyncRelayCommand(ClearHistoryAsync);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);

        _transferManager.TransferUpdated += (_, _) => RunOnUi(ReloadCollectionsFromManager);
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
        MaxConcurrentTransfers = _transferManager.Options.MaxConcurrentTransfers;
        SelectedDuplicatePolicy = _transferManager.Options.DefaultDuplicatePolicy;
        AutoDownloadIncoming = _transferManager.Options.AutoDownloadIncoming;
        AutoResumeOnReconnect = _transferManager.Options.AutoResumeOnReconnect;

        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    public void AddSelectedFiles(IEnumerable<string> filePaths)
    {
        ArgumentNullException.ThrowIfNull(filePaths);

        RunOnUi(() =>
        {
            var added = 0;
            foreach (var raw in filePaths)
            {
                if (string.IsNullOrWhiteSpace(raw))
                {
                    continue;
                }

                var full = Path.GetFullPath(raw);
                if (File.Exists(full) && !PendingSelectedFiles.Contains(full, StringComparer.OrdinalIgnoreCase))
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

        RunOnUi(() =>
        {
            if (peersResult.IsSuccess)
            {
                var peers = peersResult.Value
                    .Where(d => d.DeviceId != _localDevice.DeviceId)
                    .OrderBy(d => d.CustomName, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                var prevId = SelectedPeer?.DeviceId;
                AuthorizedPeers.Clear();
                foreach (var p in peers)
                {
                    AuthorizedPeers.Add(p);
                }

                SelectedPeer = peers.FirstOrDefault(p => p.DeviceId == prevId) ?? peers.FirstOrDefault();
            }

            ReloadCollectionsFromManager();
            SendSelectedFilesCommand.NotifyCanExecuteChanged();
        });
    }

    private async Task SendSelectedFilesAsync(CancellationToken cancellationToken)
    {
        if (SelectedPeer is null)
        {
            StatusMessage = "Selecione um dispositivo autorizado para enviar arquivos.";
            return;
        }

        if (PendingSelectedFiles.Count == 0)
        {
            StatusMessage = "Adicione ao menos um arquivo antes de iniciar o envio.";
            return;
        }

        var batch = PendingSelectedFiles.ToArray();
        var result = await _transferManager.EnqueueSendFilesAsync(
            SelectedPeer.DeviceId,
            SelectedPeer.CustomName,
            batch,
            SelectedDuplicatePolicy,
            cancellationToken).ConfigureAwait(false);

        RunOnUi(() =>
        {
            if (result.IsFailure)
            {
                StatusMessage = $"Erro ao enfileirar envio: {result.Error!.Message}";
                return;
            }

            PendingSelectedFiles.Clear();
            SendSelectedFilesCommand.NotifyCanExecuteChanged();
            ReloadCollectionsFromManager();
            StatusMessage = $"{result.Value.Count} arquivo(s) adicionado(s) à fila de envio para {SelectedPeer.CustomName}.";
        });
    }

    private void ClearPendingFiles()
    {
        PendingSelectedFiles.Clear();
        SendSelectedFilesCommand.NotifyCanExecuteChanged();
        StatusMessage = "Seleção de envio limpa.";
    }

    private async Task ExecuteControlAsync(Func<Task<Core.Errors.Result<FileTransferRecord>>> action)
    {
        var result = await action().ConfigureAwait(false);
        RunOnUi(() =>
        {
            ReloadCollectionsFromManager();
            StatusMessage = result.IsSuccess
                ? $"Transferência '{result.Value.SafeFileName}': {TransferDisplayItem.FromRecord(result.Value).StageDisplay}."
                : result.Error!.Message;
        });
    }

    private async Task ResumeInterruptedAsync(CancellationToken cancellationToken)
    {
        var result = await _transferManager.ResumeInterruptedTransfersAsync(cancellationToken).ConfigureAwait(false);
        RunOnUi(() =>
        {
            ReloadCollectionsFromManager();
            StatusMessage = result.IsSuccess
                ? $"{result.Value} transferência(s) interrompida(s) recolocada(s) na fila."
                : result.Error!.Message;
        });
    }

    private async Task SyncIncomingAsync(CancellationToken cancellationToken)
    {
        var result = await _transferManager.SyncIncomingTransfersAsync(cancellationToken).ConfigureAwait(false);
        RunOnUi(() =>
        {
            ReloadCollectionsFromManager();
            StatusMessage = result.IsSuccess
                ? $"{result.Value} transferência(s) recebida(s) sincronizada(s) do servidor."
                : result.Error!.Message;
        });
    }

    private async Task ApplyQueueConfigAsync(CancellationToken cancellationToken)
    {
        var next = _transferManager.Options with
        {
            MaxConcurrentTransfers = MaxConcurrentTransfers,
            DefaultDuplicatePolicy = SelectedDuplicatePolicy,
            AutoDownloadIncoming = AutoDownloadIncoming,
            AutoResumeOnReconnect = AutoResumeOnReconnect
        };

        var result = await _transferManager.UpdateConfigurationAsync(next, cancellationToken).ConfigureAwait(false);
        RunOnUi(() => StatusMessage = result.IsSuccess
            ? $"Configuração salva: até {next.MaxConcurrentTransfers} transferência(s) simultânea(s), duplicados = {next.DefaultDuplicatePolicy}."
            : result.Error!.Message);
    }

    private async Task ClearHistoryAsync(CancellationToken cancellationToken)
    {
        var result = await _transferManager.ClearHistoryAsync(cancellationToken).ConfigureAwait(false);
        RunOnUi(() =>
        {
            ReloadCollectionsFromManager();
            StatusMessage = result.IsSuccess
                ? $"{result.Value} registro(s) concluído(s)/encerrado(s) removido(s) do histórico."
                : result.Error!.Message;
        });
    }

    private void ReloadCollectionsFromManager()
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

        ReplaceCollection(ActiveTransfers, active);
        ReplaceCollection(ReceivedFiles, received);
        ReplaceCollection(HistoryItems, history);

        ActiveCountText = $"{active.Length} em andamento";
        ReceivedCountText = $"{received.Length} recebido(s)";
        HistoryCountText = $"{history.Length} no histórico";
    }

    private void RunOnUi(Action action)
    {
        if (_uiContext is not null && SynchronizationContext.Current != _uiContext)
        {
            _uiContext.Post(_ => action(), null);
        }
        else
        {
            action();
        }
    }

    private static void ReplaceCollection<T>(ObservableCollection<T> target, IReadOnlyList<T> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }
}
