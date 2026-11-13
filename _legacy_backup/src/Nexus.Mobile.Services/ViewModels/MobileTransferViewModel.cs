using System.Collections.ObjectModel;
using Nexus.Contracts.Security;
using Nexus.Contracts.Transfers;
using Nexus.Core.Abstractions;
using Nexus.Core.Devices;
using Nexus.Core.Errors;
using Nexus.Core.Storage;
using Nexus.Core.Transfers;
using Nexus.Mobile.Services.Mvvm;

namespace Nexus.Mobile.Services.ViewModels;

public sealed class MobileTransferViewModel : MobileObservableObject
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
    private string _statusMessage = "NEXUS Transfer pronto. Arquivos nunca são executados automaticamente.";
    private string _summaryText = "0 ativas · 0 recebidos";

    public MobileTransferViewModel(
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

        SendSelectedFilesCommand = new MobileAsyncCommand(SendSelectedFilesAsync, () => SelectedPeer is not null && PendingSelectedFiles.Count > 0);
        ClearPendingFilesCommand = new MobileAsyncCommand(_ =>
        {
            RunOnUi(() =>
            {
                PendingSelectedFiles.Clear();
                SendSelectedFilesCommand.NotifyCanExecuteChanged();
                StatusMessage = "Fila de seleção limpa.";
            });
            return Task.CompletedTask;
        });

        PauseTransferCommand = new MobileAsyncCommand<TransferDisplayItem>((item, ct) =>
            item is null ? Task.CompletedTask : ExecuteControlAsync(() => _transferManager.PauseTransferAsync(item.TransferId, ct)));

        ResumeTransferCommand = new MobileAsyncCommand<TransferDisplayItem>((item, ct) =>
            item is null ? Task.CompletedTask : ExecuteControlAsync(() => _transferManager.ResumeTransferAsync(item.TransferId, ct)));

        CancelTransferCommand = new MobileAsyncCommand<TransferDisplayItem>((item, ct) =>
            item is null ? Task.CompletedTask : ExecuteControlAsync(() => _transferManager.CancelTransferAsync(item.TransferId, null, ct)));

        SyncIncomingCommand = new MobileAsyncCommand(SyncIncomingAsync);
        ResumeInterruptedCommand = new MobileAsyncCommand(ResumeInterruptedAsync);
        SaveSettingsCommand = new MobileAsyncCommand(SaveSettingsAsync);
        ClearHistoryCommand = new MobileAsyncCommand(ClearHistoryAsync);
        RefreshCommand = new MobileAsyncCommand(RefreshAsync);

        _transferManager.TransferUpdated += (_, _) => RunOnUi(ReloadCollections);
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

    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public string SummaryText { get => _summaryText; private set => SetProperty(ref _summaryText, value); }

    public MobileAsyncCommand SendSelectedFilesCommand { get; }
    public MobileAsyncCommand ClearPendingFilesCommand { get; }
    public MobileAsyncCommand<TransferDisplayItem> PauseTransferCommand { get; }
    public MobileAsyncCommand<TransferDisplayItem> ResumeTransferCommand { get; }
    public MobileAsyncCommand<TransferDisplayItem> CancelTransferCommand { get; }
    public MobileAsyncCommand SyncIncomingCommand { get; }
    public MobileAsyncCommand ResumeInterruptedCommand { get; }
    public MobileAsyncCommand SaveSettingsCommand { get; }
    public MobileAsyncCommand ClearHistoryCommand { get; }
    public MobileAsyncCommand RefreshCommand { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _transferManager.InitializeAsync(cancellationToken).ConfigureAwait(false);
        MaxConcurrentTransfers = _transferManager.Options.MaxConcurrentTransfers;
        SelectedDuplicatePolicy = _transferManager.Options.DefaultDuplicatePolicy;
        AutoDownloadIncoming = _transferManager.Options.AutoDownloadIncoming;
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    public void AddSelectedFiles(IEnumerable<string> filePaths)
    {
        ArgumentNullException.ThrowIfNull(filePaths);

        RunOnUi(() =>
        {
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
                }
            }

            StatusMessage = $"{PendingSelectedFiles.Count} arquivo(s) selecionado(s) para enviar.";
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

            ReloadCollections();
            SendSelectedFilesCommand.NotifyCanExecuteChanged();
        });
    }

    private async Task SendSelectedFilesAsync(CancellationToken cancellationToken)
    {
        if (SelectedPeer is null || PendingSelectedFiles.Count == 0)
        {
            StatusMessage = "Selecione destino autorizado e ao menos um arquivo.";
            return;
        }

        var files = PendingSelectedFiles.ToArray();
        var result = await _transferManager.EnqueueSendFilesAsync(
            SelectedPeer.DeviceId,
            SelectedPeer.CustomName,
            files,
            SelectedDuplicatePolicy,
            cancellationToken).ConfigureAwait(false);

        RunOnUi(() =>
        {
            if (result.IsFailure)
            {
                StatusMessage = result.Error!.Message;
                return;
            }

            PendingSelectedFiles.Clear();
            SendSelectedFilesCommand.NotifyCanExecuteChanged();
            ReloadCollections();
            StatusMessage = $"{result.Value.Count} arquivo(s) na fila de envio para {SelectedPeer.CustomName}.";
        });
    }

    private async Task ExecuteControlAsync(Func<Task<Result<FileTransferRecord>>> action)
    {
        var res = await action().ConfigureAwait(false);
        RunOnUi(() =>
        {
            ReloadCollections();
            StatusMessage = res.IsSuccess
                ? $"{res.Value.SafeFileName}: {TransferDisplayItem.FromRecord(res.Value).StageDisplay}."
                : res.Error!.Message;
        });
    }

    private async Task SyncIncomingAsync(CancellationToken cancellationToken)
    {
        var result = await _transferManager.SyncIncomingTransfersAsync(cancellationToken).ConfigureAwait(false);
        RunOnUi(() =>
        {
            ReloadCollections();
            StatusMessage = result.IsSuccess
                ? $"{result.Value} recebimento(s) sincronizado(s)."
                : result.Error!.Message;
        });
    }

    private async Task ResumeInterruptedAsync(CancellationToken cancellationToken)
    {
        var result = await _transferManager.ResumeInterruptedTransfersAsync(cancellationToken).ConfigureAwait(false);
        RunOnUi(() =>
        {
            ReloadCollections();
            StatusMessage = result.IsSuccess
                ? $"{result.Value} transferência(s) retomada(s)."
                : result.Error!.Message;
        });
    }

    private async Task SaveSettingsAsync(CancellationToken cancellationToken)
    {
        var next = _transferManager.Options with
        {
            MaxConcurrentTransfers = MaxConcurrentTransfers,
            DefaultDuplicatePolicy = SelectedDuplicatePolicy,
            AutoDownloadIncoming = AutoDownloadIncoming
        };

        var result = await _transferManager.UpdateConfigurationAsync(next, cancellationToken).ConfigureAwait(false);
        RunOnUi(() => StatusMessage = result.IsSuccess
            ? $"Limite simultâneo: {next.MaxConcurrentTransfers}, duplicados: {next.DefaultDuplicatePolicy}."
            : result.Error!.Message);
    }

    private async Task ClearHistoryAsync(CancellationToken cancellationToken)
    {
        var result = await _transferManager.ClearHistoryAsync(cancellationToken).ConfigureAwait(false);
        RunOnUi(() =>
        {
            ReloadCollections();
            StatusMessage = result.IsSuccess
                ? $"{result.Value} item(ns) removido(s) do histórico."
                : result.Error!.Message;
        });
    }

    private void ReloadCollections()
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

        var history = all.Select(TransferDisplayItem.FromRecord).ToArray();

        ReplaceCollection(ActiveTransfers, active);
        ReplaceCollection(ReceivedFiles, received);
        ReplaceCollection(HistoryItems, history);
        SummaryText = $"{active.Length} ativas · {received.Length} recebidos · {history.Length} no histórico";
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
