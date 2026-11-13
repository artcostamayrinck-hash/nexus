using System.Collections.ObjectModel;
using Nexus.Contracts.Sync;
using Nexus.Core.Sync;
using Nexus.Desktop.Services.Mvvm;

namespace Nexus.Desktop.Services.ViewModels;

public sealed class SyncViewModel : ObservableObject, IDisposable
{
    private readonly IClipboardSyncService _clipboardService;
    private readonly INotesSyncService _notesService;
    private readonly ISyncOrchestrator _syncOrchestrator;
    private readonly ISyncPrivacySettingsService _privacyService;
    private readonly ISystemClipboardAdapter _clipboardAdapter;

    private long _notesRefreshGeneration;

    private string _syncStatusText = "Ocioso";
    private string _lastSyncText = "Nunca sincronizado";
    private string _pendingQueueText = "0 pendente(s)";
    private string _conflictsCountText = "0 conflito(s)";
    private string _platformCapabilityText;
    private string _statusBanner = "Clipboard e Notas prontos para sincronização segura.";

    private bool _clipboardSyncEnabled = true;
    private bool _clipboardAutoSync = true;
    private int _clipboardMaxHistoryItems = 50;
    private bool _notesSyncEnabled = true;
    private bool _notesAutoSync = true;
    private ConflictResolutionPolicy _selectedConflictPolicy = ConflictResolutionPolicy.Manual;

    private string _clipboardInputText = string.Empty;
    private string _searchQuery = string.Empty;
    private NexusNoteContract? _selectedNote;
    private string _editorTitle = string.Empty;
    private string _editorBody = string.Empty;
    private string _editorMetadataText = "Selecione uma nota ou crie uma nova.";

    private SyncConflictRecord? _selectedConflict;
    private string _conflictMergedTitle = string.Empty;
    private string _conflictMergedBody = string.Empty;

    public SyncViewModel(
        IClipboardSyncService clipboardService,
        INotesSyncService notesService,
        ISyncOrchestrator syncOrchestrator,
        ISyncPrivacySettingsService privacyService,
        ISystemClipboardAdapter clipboardAdapter,
        IUiDispatcher? uiDispatcher = null)
        : base(uiDispatcher)
    {
        _clipboardService = clipboardService;
        _notesService = notesService;
        _syncOrchestrator = syncOrchestrator;
        _privacyService = privacyService;
        _clipboardAdapter = clipboardAdapter;
        _platformCapabilityText = clipboardAdapter.PlatformRestrictionNotice;

        AvailablePolicies =
        [
            ConflictResolutionPolicy.Manual,
            ConflictResolutionPolicy.LastWriteWins,
            ConflictResolutionPolicy.PreferLocal,
            ConflictResolutionPolicy.PreferRemote
        ];

        SyncNowCommand = new AsyncRelayCommand(SyncNowAsync, uiDispatcher: uiDispatcher);
        SavePreferencesCommand = new AsyncRelayCommand(SavePreferencesAsync, uiDispatcher: uiDispatcher);
        SendClipboardTextCommand = new AsyncRelayCommand(SendClipboardTextAsync, uiDispatcher: uiDispatcher);
        CaptureOsClipboardCommand = new AsyncRelayCommand(CaptureOsClipboardAsync, uiDispatcher: uiDispatcher);
        CopyHistoryEntryCommand = new AsyncRelayCommand<ClipboardEntryContract>(CopyHistoryEntryAsync, uiDispatcher: uiDispatcher);
        ClearClipboardHistoryCommand = new AsyncRelayCommand(ClearClipboardHistoryAsync, uiDispatcher: uiDispatcher);
        SearchNotesCommand = new AsyncRelayCommand(RefreshNotesAndConflictsAsync, uiDispatcher: uiDispatcher);
        NewNoteCommand = new RelayCommand(StartNewNote, uiDispatcher: uiDispatcher);
        SaveNoteCommand = new AsyncRelayCommand(SaveNoteAsync, uiDispatcher: uiDispatcher);
        DeleteSelectedNoteCommand = new AsyncRelayCommand(DeleteSelectedNoteAsync, uiDispatcher: uiDispatcher);
        RestoreRevisionCommand = new AsyncRelayCommand<NoteRevisionSnapshot>(RestoreRevisionAsync, uiDispatcher: uiDispatcher);
        KeepLocalConflictCommand = new AsyncRelayCommand(_ => ResolveSelectedConflictAsync(ConflictResolutionChoice.KeepLocal), uiDispatcher: uiDispatcher);
        AcceptRemoteConflictCommand = new AsyncRelayCommand(_ => ResolveSelectedConflictAsync(ConflictResolutionChoice.AcceptRemote), uiDispatcher: uiDispatcher);
        ManualMergeConflictCommand = new AsyncRelayCommand(_ => ResolveSelectedConflictAsync(ConflictResolutionChoice.ManualMerge), uiDispatcher: uiDispatcher);

        _syncOrchestrator.OverviewChanged += OnOverviewChanged;
        _clipboardService.HistoryChanged += OnClipboardHistoryChanged;
        _notesService.NotesOrConflictsChanged += OnNotesOrConflictsChanged;
    }

    public ObservableCollection<ClipboardEntryContract> ClipboardHistory { get; } = new();
    public ObservableCollection<NexusNoteContract> Notes { get; } = new();
    public ObservableCollection<NoteRevisionSnapshot> SelectedNoteRevisions { get; } = new();
    public ObservableCollection<SyncConflictRecord> UnresolvedConflicts { get; } = new();
    public IReadOnlyList<ConflictResolutionPolicy> AvailablePolicies { get; }

    public string SyncStatusText { get => _syncStatusText; private set => SetProperty(ref _syncStatusText, value); }
    public string LastSyncText { get => _lastSyncText; private set => SetProperty(ref _lastSyncText, value); }
    public string PendingQueueText { get => _pendingQueueText; private set => SetProperty(ref _pendingQueueText, value); }
    public string ConflictsCountText { get => _conflictsCountText; private set => SetProperty(ref _conflictsCountText, value); }
    public string PlatformCapabilityText { get => _platformCapabilityText; private set => SetProperty(ref _platformCapabilityText, value); }
    public string StatusBanner { get => _statusBanner; private set => SetProperty(ref _statusBanner, value); }

    public bool ClipboardSyncEnabled { get => _clipboardSyncEnabled; set => SetProperty(ref _clipboardSyncEnabled, value); }
    public bool ClipboardAutoSync { get => _clipboardAutoSync; set => SetProperty(ref _clipboardAutoSync, value); }
    public int ClipboardMaxHistoryItems { get => _clipboardMaxHistoryItems; set => SetProperty(ref _clipboardMaxHistoryItems, value); }
    public bool NotesSyncEnabled { get => _notesSyncEnabled; set => SetProperty(ref _notesSyncEnabled, value); }
    public bool NotesAutoSync { get => _notesAutoSync; set => SetProperty(ref _notesAutoSync, value); }
    public ConflictResolutionPolicy SelectedConflictPolicy { get => _selectedConflictPolicy; set => SetProperty(ref _selectedConflictPolicy, value); }

    public string ClipboardInputText { get => _clipboardInputText; set => SetProperty(ref _clipboardInputText, value); }

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (SetProperty(ref _searchQuery, value))
            {
                _ = RefreshNotesAndConflictsSafelyAsync();
            }
        }
    }

    public NexusNoteContract? SelectedNote
    {
        get => _selectedNote;
        set
        {
            if (SetProperty(ref _selectedNote, value))
            {
                if (value is not null)
                {
                    EditorTitle = value.Title;
                    EditorBody = value.Body;
                    EditorMetadataText =
                        $"v{value.Version} (base {value.BaseVersion}) · Criada: {value.CreatedAtUtc.ToLocalTime():g} · Alterada: {value.ModifiedAtUtc.ToLocalTime():g} por {value.LastModifiedByDeviceName}" +
                        (value.HasConflict ? " · EM CONFLITO" : string.Empty);
                    _ = LoadRevisionsForNoteSafelyAsync(value.NoteId);
                }
                else
                {
                    SelectedNoteRevisions.Clear();
                }
            }
        }
    }

    public string EditorTitle { get => _editorTitle; set => SetProperty(ref _editorTitle, value); }
    public string EditorBody { get => _editorBody; set => SetProperty(ref _editorBody, value); }
    public string EditorMetadataText { get => _editorMetadataText; private set => SetProperty(ref _editorMetadataText, value); }

    public SyncConflictRecord? SelectedConflict
    {
        get => _selectedConflict;
        set
        {
            if (SetProperty(ref _selectedConflict, value) && value is not null)
            {
                ConflictMergedTitle = value.LocalTitle;
                ConflictMergedBody = $"{value.LocalBody}\n\n--- Mesclado com ({value.RemoteDeviceName}) ---\n{value.RemoteBody}";
            }
        }
    }

    public string ConflictMergedTitle { get => _conflictMergedTitle; set => SetProperty(ref _conflictMergedTitle, value); }
    public string ConflictMergedBody { get => _conflictMergedBody; set => SetProperty(ref _conflictMergedBody, value); }

    public AsyncRelayCommand SyncNowCommand { get; }
    public AsyncRelayCommand SavePreferencesCommand { get; }
    public AsyncRelayCommand SendClipboardTextCommand { get; }
    public AsyncRelayCommand CaptureOsClipboardCommand { get; }
    public AsyncRelayCommand<ClipboardEntryContract> CopyHistoryEntryCommand { get; }
    public AsyncRelayCommand ClearClipboardHistoryCommand { get; }
    public AsyncRelayCommand SearchNotesCommand { get; }
    public RelayCommand NewNoteCommand { get; }
    public AsyncRelayCommand SaveNoteCommand { get; }
    public AsyncRelayCommand DeleteSelectedNoteCommand { get; }
    public AsyncRelayCommand<NoteRevisionSnapshot> RestoreRevisionCommand { get; }
    public AsyncRelayCommand KeepLocalConflictCommand { get; }
    public AsyncRelayCommand AcceptRemoteConflictCommand { get; }
    public AsyncRelayCommand ManualMergeConflictCommand { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var prefsResult = await _privacyService.GetPreferencesAsync(cancellationToken).ConfigureAwait(false);
        var initOverview = await _syncOrchestrator.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var history = await _clipboardService.GetHistoryAsync(cancellationToken).ConfigureAwait(false);

        await RunOnUiAsync(() =>
        {
            if (prefsResult.IsSuccess)
            {
                var prefs = prefsResult.Value;
                ClipboardSyncEnabled = prefs.ClipboardSyncEnabled;
                ClipboardAutoSync = prefs.ClipboardAutoSync;
                ClipboardMaxHistoryItems = prefs.ClipboardMaxHistoryItems;
                NotesSyncEnabled = prefs.NotesSyncEnabled;
                NotesAutoSync = prefs.NotesAutoSync;
                SelectedConflictPolicy = prefs.DefaultNoteConflictPolicy;
            }

            if (initOverview.IsSuccess)
            {
                ApplyOverview(initOverview.Value);
            }

            if (history.IsSuccess)
            {
                ReplaceCollection(ClipboardHistory, history.Value);
            }
        }, cancellationToken).ConfigureAwait(false);

        await RefreshNotesAndConflictsAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task SyncNowAsync(CancellationToken ct)
    {
        await RunOnUiAsync(
            () => StatusBanner = "Sincronizando alterações locais e fila offline...",
            ct).ConfigureAwait(false);

        var res = await _syncOrchestrator.SyncNowAsync(ct).ConfigureAwait(false);
        if (res.IsFailure)
        {
            var errMsg = $"Sincronização pendente/falha: {res.Error?.Message}";
            await RunOnUiAsync(() => StatusBanner = errMsg, ct).ConfigureAwait(false);
            return;
        }

        var overview = res.Value;
        await RefreshNotesAndConflictsAsync(ct).ConfigureAwait(false);
        var history = await _clipboardService.GetHistoryAsync(ct).ConfigureAwait(false);
        var historySnapshot = history.IsSuccess ? history.Value : null;

        await RunOnUiAsync(() =>
        {
            ApplyOverview(overview);
            if (historySnapshot is not null)
            {
                ReplaceCollection(ClipboardHistory, historySnapshot);
            }

            StatusBanner = overview.LastDiagnostic ?? "Sincronização concluída.";
        }, ct).ConfigureAwait(false);
    }

    private async Task SavePreferencesAsync(CancellationToken ct)
    {
        SyncPrivacyPreferences updated = new();
        await RunOnUiAsync(() =>
        {
            updated = new SyncPrivacyPreferences
            {
                ClipboardSyncEnabled = ClipboardSyncEnabled,
                ClipboardAutoSync = ClipboardAutoSync,
                ClipboardMaxHistoryItems = Math.Clamp(ClipboardMaxHistoryItems, 1, 500),
                NotesSyncEnabled = NotesSyncEnabled,
                NotesAutoSync = NotesAutoSync,
                DefaultNoteConflictPolicy = SelectedConflictPolicy
            };
        }, ct).ConfigureAwait(false);

        var saved = await _privacyService.SavePreferencesAsync(updated, ct).ConfigureAwait(false);
        var msg = saved.IsSuccess
            ? "Preferências de privacidade e resolução de conflitos salvas."
            : $"Erro ao salvar preferências: {saved.Error?.Message}";

        await RunOnUiAsync(() => StatusBanner = msg, ct).ConfigureAwait(false);
    }

    private async Task SendClipboardTextAsync(CancellationToken ct)
    {
        string text = string.Empty;
        await RunOnUiAsync(() => text = ClipboardInputText, ct).ConfigureAwait(false);

        var res = await _clipboardService.CaptureAndSendTextAsync(text, manualTrigger: true, ct).ConfigureAwait(false);
        if (res.IsSuccess)
        {
            var captured = res.Value;
            await RunOnUiAsync(() =>
            {
                ClipboardInputText = string.Empty;
                StatusBanner = $"Texto enviado ao Clipboard NEXUS ({captured.CapturedAtUtc.ToLocalTime():T}).";
            }, ct).ConfigureAwait(false);
        }
        else
        {
            var errMsg = $"Clipboard: {res.Error?.Message}";
            await RunOnUiAsync(() => StatusBanner = errMsg, ct).ConfigureAwait(false);
        }
    }

    private async Task CaptureOsClipboardAsync(CancellationToken ct)
    {
        var res = await _clipboardService.CaptureCurrentOsClipboardAsync(ct).ConfigureAwait(false);
        var msg = res.IsFailure
            ? $"Captura OS: {res.Error?.Message}"
            : res.Value is null
                ? "Clipboard do Windows está vazio ou sem texto."
                : "Conteúdo atual do Windows capturado e enfileirado para sincronização.";

        await RunOnUiAsync(() => StatusBanner = msg, ct).ConfigureAwait(false);
    }

    private async Task CopyHistoryEntryAsync(ClipboardEntryContract? entry, CancellationToken ct)
    {
        if (entry is null)
        {
            return;
        }

        var write = await _clipboardAdapter.WriteTextAsync(entry.ContentText, ct).ConfigureAwait(false);
        var msg = write.IsSuccess
            ? $"Copiado para a área de transferência local ({entry.OriginDeviceName})."
            : $"Falha ao copiar: {write.Error?.Message}";

        await RunOnUiAsync(() => StatusBanner = msg, ct).ConfigureAwait(false);
    }

    private async Task ClearClipboardHistoryAsync(CancellationToken ct)
    {
        await _clipboardService.ClearHistoryAsync(ct).ConfigureAwait(false);
        await RunOnUiAsync(() => StatusBanner = "Histórico local de clipboard limpo.", ct).ConfigureAwait(false);
    }

    private void StartNewNote()
    {
        RunOnUi(() =>
        {
            SelectedNote = null;
            EditorTitle = string.Empty;
            EditorBody = string.Empty;
            EditorMetadataText = "Nova nota (será salva localmente e sincronizada conforme suas preferências).";
        });
    }

    private async Task SaveNoteAsync(CancellationToken ct)
    {
        string title = string.Empty;
        string body = string.Empty;
        NexusNoteContract? selected = null;

        await RunOnUiAsync(() =>
        {
            title = EditorTitle;
            body = EditorBody;
            selected = SelectedNote;
        }, ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(title))
        {
            await RunOnUiAsync(() => StatusBanner = "Informe um título antes de salvar a nota.", ct).ConfigureAwait(false);
            return;
        }

        var res = selected is null
            ? await _notesService.CreateNoteAsync(title, body, ct).ConfigureAwait(false)
            : await _notesService.UpdateNoteAsync(selected.NoteId, title, body, ct).ConfigureAwait(false);

        if (res.IsFailure)
        {
            var errMsg = $"Erro ao salvar nota: {res.Error?.Message}";
            await RunOnUiAsync(() => StatusBanner = errMsg, ct).ConfigureAwait(false);
            return;
        }

        var savedNote = res.Value;
        await RefreshNotesAndConflictsAsync(ct).ConfigureAwait(false);
        await RunOnUiAsync(() =>
        {
            SelectedNote = Notes.FirstOrDefault(n => n.NoteId == savedNote.NoteId);
            StatusBanner = $"Nota '{savedNote.Title}' salva (v{savedNote.Version}).";
        }, ct).ConfigureAwait(false);
    }

    private async Task DeleteSelectedNoteAsync(CancellationToken ct)
    {
        Guid? selectedId = null;
        await RunOnUiAsync(() => selectedId = SelectedNote?.NoteId, ct).ConfigureAwait(false);

        if (!selectedId.HasValue)
        {
            await RunOnUiAsync(() => StatusBanner = "Selecione uma nota para excluir.", ct).ConfigureAwait(false);
            return;
        }

        var res = await _notesService.DeleteNoteAsync(selectedId.Value, ct).ConfigureAwait(false);
        if (res.IsFailure)
        {
            var errMsg = $"Erro ao excluir: {res.Error?.Message}";
            await RunOnUiAsync(() => StatusBanner = errMsg, ct).ConfigureAwait(false);
            return;
        }

        var deleted = res.Value;
        StartNewNote();
        await RefreshNotesAndConflictsAsync(ct).ConfigureAwait(false);
        await RunOnUiAsync(
            () => StatusBanner = $"Nota marcada como excluída (tombstone v{deleted.Version}).",
            ct).ConfigureAwait(false);
    }

    private async Task RestoreRevisionAsync(NoteRevisionSnapshot? revision, CancellationToken ct)
    {
        if (revision is null)
        {
            return;
        }

        var res = await _notesService.RestoreRevisionAsync(revision.NoteId, revision.RevisionId, ct).ConfigureAwait(false);
        if (res.IsFailure)
        {
            var errMsg = $"Erro ao restaurar versão: {res.Error?.Message}";
            await RunOnUiAsync(() => StatusBanner = errMsg, ct).ConfigureAwait(false);
            return;
        }

        var restored = res.Value;
        await RefreshNotesAndConflictsAsync(ct).ConfigureAwait(false);
        await RunOnUiAsync(() =>
        {
            SelectedNote = Notes.FirstOrDefault(n => n.NoteId == restored.NoteId);
            StatusBanner = $"Versão v{revision.Version} restaurada como v{restored.Version}.";
        }, ct).ConfigureAwait(false);
    }

    private async Task ResolveSelectedConflictAsync(ConflictResolutionChoice choice)
    {
        SyncConflictRecord? conflict = null;
        string mergedTitle = string.Empty;
        string mergedBody = string.Empty;

        await RunOnUiAsync(() =>
        {
            conflict = SelectedConflict ?? UnresolvedConflicts.FirstOrDefault();
            mergedTitle = ConflictMergedTitle;
            mergedBody = ConflictMergedBody;
        }).ConfigureAwait(false);

        if (conflict is null)
        {
            await RunOnUiAsync(() => StatusBanner = "Nenhum conflito selecionado para resolver.").ConfigureAwait(false);
            return;
        }

        var request = new ResolveConflictRequest
        {
            ConflictId = conflict.ConflictId,
            Choice = choice,
            MergedTitle = choice == ConflictResolutionChoice.ManualMerge ? mergedTitle : null,
            MergedBody = choice == ConflictResolutionChoice.ManualMerge ? mergedBody : null
        };

        var res = await _notesService.ResolveConflictAsync(request).ConfigureAwait(false);
        if (res.IsFailure)
        {
            var errMsg = $"Erro ao resolver conflito: {res.Error?.Message}";
            await RunOnUiAsync(() => StatusBanner = errMsg).ConfigureAwait(false);
            return;
        }

        var resolvedNote = res.Value;
        await RefreshNotesAndConflictsAsync(CancellationToken.None).ConfigureAwait(false);
        await RunOnUiAsync(
            () => StatusBanner = $"Conflito resolvido ({choice}) -> Nota v{resolvedNote.Version}.").ConfigureAwait(false);
    }

    private async Task RefreshNotesAndConflictsSafelyAsync()
    {
        try
        {
            await RefreshNotesAndConflictsAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Ignorado durante cancelamento.
        }
    }

    private async Task LoadRevisionsForNoteSafelyAsync(Guid noteId)
    {
        try
        {
            await LoadRevisionsForNoteAsync(noteId).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Ignorado durante cancelamento.
        }
    }

    private async Task RefreshNotesAndConflictsAsync(CancellationToken ct)
    {
        var generation = Interlocked.Increment(ref _notesRefreshGeneration);
        var query = SearchQuery;

        var notesRes = await _notesService.ListNotesAsync(query, includeDeleted: false, ct).ConfigureAwait(false);
        var conflictsRes = await _notesService.ListConflictsAsync(unresolvedOnly: true, ct).ConfigureAwait(false);

        Guid? selectedNoteToLoadRevs = null;

        await RunOnUiAsync(() =>
        {
            if (generation != Volatile.Read(ref _notesRefreshGeneration))
            {
                return;
            }

            if (notesRes.IsSuccess)
            {
                var currentId = SelectedNote?.NoteId;
                ReplaceCollection(Notes, notesRes.Value);
                if (currentId.HasValue)
                {
                    var match = Notes.FirstOrDefault(n => n.NoteId == currentId.Value);
                    if (match is not null)
                    {
                        _selectedNote = match;
                        OnPropertyChanged(nameof(SelectedNote));
                        selectedNoteToLoadRevs = match.NoteId;
                    }
                }
            }

            if (conflictsRes.IsSuccess)
            {
                ReplaceCollection(UnresolvedConflicts, conflictsRes.Value);
                if (SelectedConflict is null || !UnresolvedConflicts.Any(c => c.ConflictId == SelectedConflict.ConflictId))
                {
                    SelectedConflict = UnresolvedConflicts.FirstOrDefault();
                }
            }
        }, ct).ConfigureAwait(false);

        if (selectedNoteToLoadRevs.HasValue)
        {
            await LoadRevisionsForNoteAsync(selectedNoteToLoadRevs.Value, ct).ConfigureAwait(false);
        }
    }

    private async Task LoadRevisionsForNoteAsync(Guid noteId, CancellationToken ct = default)
    {
        var revs = await _notesService.GetRevisionsAsync(noteId, ct).ConfigureAwait(false);
        if (revs.IsSuccess)
        {
            var items = revs.Value;
            await RunOnUiAsync(() =>
            {
                if (SelectedNote?.NoteId == noteId)
                {
                    ReplaceCollection(SelectedNoteRevisions, items);
                }
            }, ct).ConfigureAwait(false);
        }
    }

    private void OnOverviewChanged(object? sender, SyncOverviewSnapshot overview)
        => RunOnUi(() => ApplyOverview(overview));

    private void OnClipboardHistoryChanged(object? sender, IReadOnlyList<ClipboardEntryContract> history)
    {
        var snapshot = history.ToArray();
        RunOnUi(() => ReplaceCollection(ClipboardHistory, snapshot));
    }

    private void OnNotesOrConflictsChanged(object? sender, EventArgs e)
        => _ = RefreshNotesAndConflictsSafelyAsync();

    private void ApplyOverview(SyncOverviewSnapshot overview)
    {
        SyncStatusText = overview.Status.ToString();
        LastSyncText = overview.LastSyncAtUtc.HasValue
            ? overview.LastSyncAtUtc.Value.ToLocalTime().ToString("g")
            : "Nunca sincronizado";
        PendingQueueText = $"{overview.PendingOperationsCount} na fila offline";
        ConflictsCountText = $"{overview.UnresolvedConflictsCount} conflito(s)";
    }

    public void Dispose()
    {
        _syncOrchestrator.OverviewChanged -= OnOverviewChanged;
        _clipboardService.HistoryChanged -= OnClipboardHistoryChanged;
        _notesService.NotesOrConflictsChanged -= OnNotesOrConflictsChanged;
    }
}
