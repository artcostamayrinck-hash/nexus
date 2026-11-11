using System.Collections.ObjectModel;
using Nexus.Contracts.Sync;
using Nexus.Core.Sync;
using Nexus.Desktop.Services.Mvvm;

namespace Nexus.Desktop.Services.ViewModels;

public sealed class SyncViewModel : ObservableObject
{
    private readonly IClipboardSyncService _clipboardService;
    private readonly INotesSyncService _notesService;
    private readonly ISyncOrchestrator _syncOrchestrator;
    private readonly ISyncPrivacySettingsService _privacyService;
    private readonly ISystemClipboardAdapter _clipboardAdapter;
    private readonly SynchronizationContext? _uiContext;

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
        ISystemClipboardAdapter clipboardAdapter)
    {
        _clipboardService = clipboardService;
        _notesService = notesService;
        _syncOrchestrator = syncOrchestrator;
        _privacyService = privacyService;
        _clipboardAdapter = clipboardAdapter;
        _uiContext = SynchronizationContext.Current;
        _platformCapabilityText = clipboardAdapter.PlatformRestrictionNotice;

        AvailablePolicies =
        [
            ConflictResolutionPolicy.Manual,
            ConflictResolutionPolicy.LastWriteWins,
            ConflictResolutionPolicy.PreferLocal,
            ConflictResolutionPolicy.PreferRemote
        ];

        SyncNowCommand = new AsyncRelayCommand(SyncNowAsync);
        SavePreferencesCommand = new AsyncRelayCommand(SavePreferencesAsync);
        SendClipboardTextCommand = new AsyncRelayCommand(SendClipboardTextAsync);
        CaptureOsClipboardCommand = new AsyncRelayCommand(CaptureOsClipboardAsync);
        CopyHistoryEntryCommand = new AsyncRelayCommand<ClipboardEntryContract>(CopyHistoryEntryAsync);
        ClearClipboardHistoryCommand = new AsyncRelayCommand(ClearClipboardHistoryAsync);
        SearchNotesCommand = new AsyncRelayCommand(RefreshNotesAndConflictsAsync);
        NewNoteCommand = new RelayCommand(StartNewNote);
        SaveNoteCommand = new AsyncRelayCommand(SaveNoteAsync);
        DeleteSelectedNoteCommand = new AsyncRelayCommand(DeleteSelectedNoteAsync);
        RestoreRevisionCommand = new AsyncRelayCommand<NoteRevisionSnapshot>(RestoreRevisionAsync);
        KeepLocalConflictCommand = new AsyncRelayCommand(_ => ResolveSelectedConflictAsync(ConflictResolutionChoice.KeepLocal));
        AcceptRemoteConflictCommand = new AsyncRelayCommand(_ => ResolveSelectedConflictAsync(ConflictResolutionChoice.AcceptRemote));
        ManualMergeConflictCommand = new AsyncRelayCommand(_ => ResolveSelectedConflictAsync(ConflictResolutionChoice.ManualMerge));

        _syncOrchestrator.OverviewChanged += (_, overview) => RunOnUi(() => ApplyOverview(overview));
        _clipboardService.HistoryChanged += (_, history) => RunOnUi(() => ReplaceCollection(ClipboardHistory, history));
        _notesService.NotesOrConflictsChanged += (_, _) => _ = RunOnUiAsync(RefreshNotesAndConflictsAsync);
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
                _ = RefreshNotesAndConflictsAsync(CancellationToken.None);
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
                    _ = LoadRevisionsForNoteAsync(value.NoteId);
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
        if (prefsResult.IsSuccess)
        {
            var prefs = prefsResult.Value;
            RunOnUi(() =>
            {
                ClipboardSyncEnabled = prefs.ClipboardSyncEnabled;
                ClipboardAutoSync = prefs.ClipboardAutoSync;
                ClipboardMaxHistoryItems = prefs.ClipboardMaxHistoryItems;
                NotesSyncEnabled = prefs.NotesSyncEnabled;
                NotesAutoSync = prefs.NotesAutoSync;
                SelectedConflictPolicy = prefs.DefaultNoteConflictPolicy;
            });
        }

        var initOverview = await _syncOrchestrator.InitializeAsync(cancellationToken).ConfigureAwait(false);
        if (initOverview.IsSuccess)
        {
            RunOnUi(() => ApplyOverview(initOverview.Value));
        }

        var history = await _clipboardService.GetHistoryAsync(cancellationToken).ConfigureAwait(false);
        if (history.IsSuccess)
        {
            RunOnUi(() => ReplaceCollection(ClipboardHistory, history.Value));
        }

        await RefreshNotesAndConflictsAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task SyncNowAsync(CancellationToken ct)
    {
        StatusBanner = "Sincronizando alterações locais e fila offline...";
        var res = await _syncOrchestrator.SyncNowAsync(ct).ConfigureAwait(false);
        if (res.IsSuccess)
        {
            RunOnUi(() => ApplyOverview(res.Value));
            await RefreshNotesAndConflictsAsync(ct).ConfigureAwait(false);
            var history = await _clipboardService.GetHistoryAsync(ct).ConfigureAwait(false);
            if (history.IsSuccess)
            {
                RunOnUi(() => ReplaceCollection(ClipboardHistory, history.Value));
            }
            StatusBanner = res.Value.LastDiagnostic ?? "Sincronização concluída.";
        }
        else
        {
            StatusBanner = $"Sincronização pendente/falha: {res.Error?.Message}";
        }
    }

    private async Task SavePreferencesAsync(CancellationToken ct)
    {
        var updated = new SyncPrivacyPreferences
        {
            ClipboardSyncEnabled = ClipboardSyncEnabled,
            ClipboardAutoSync = ClipboardAutoSync,
            ClipboardMaxHistoryItems = Math.Clamp(ClipboardMaxHistoryItems, 1, 500),
            NotesSyncEnabled = NotesSyncEnabled,
            NotesAutoSync = NotesAutoSync,
            DefaultNoteConflictPolicy = SelectedConflictPolicy
        };

        var saved = await _privacyService.SavePreferencesAsync(updated, ct).ConfigureAwait(false);
        StatusBanner = saved.IsSuccess
            ? "Preferências de privacidade e resolução de conflitos salvas."
            : $"Erro ao salvar preferências: {saved.Error?.Message}";
    }

    private async Task SendClipboardTextAsync(CancellationToken ct)
    {
        var text = ClipboardInputText;
        var res = await _clipboardService.CaptureAndSendTextAsync(text, manualTrigger: true, ct).ConfigureAwait(false);
        if (res.IsSuccess)
        {
            RunOnUi(() => ClipboardInputText = string.Empty);
            StatusBanner = $"Texto enviado ao Clipboard NEXUS ({res.Value.CapturedAtUtc.ToLocalTime():T}).";
        }
        else
        {
            StatusBanner = $"Clipboard: {res.Error?.Message}";
        }
    }

    private async Task CaptureOsClipboardAsync(CancellationToken ct)
    {
        var res = await _clipboardService.CaptureCurrentOsClipboardAsync(ct).ConfigureAwait(false);
        if (res.IsFailure)
        {
            StatusBanner = $"Captura OS: {res.Error?.Message}";
            return;
        }

        StatusBanner = res.Value is null
            ? "Clipboard do Windows está vazio ou sem texto."
            : "Conteúdo atual do Windows capturado e enfileirado para sincronização.";
    }

    private async Task CopyHistoryEntryAsync(ClipboardEntryContract? entry, CancellationToken ct)
    {
        if (entry is null)
        {
            return;
        }

        var write = await _clipboardAdapter.WriteTextAsync(entry.ContentText, ct).ConfigureAwait(false);
        StatusBanner = write.IsSuccess
            ? $"Copiado para a área de transferência local ({entry.OriginDeviceName})."
            : $"Falha ao copiar: {write.Error?.Message}";
    }

    private async Task ClearClipboardHistoryAsync(CancellationToken ct)
    {
        await _clipboardService.ClearHistoryAsync(ct).ConfigureAwait(false);
        StatusBanner = "Histórico local de clipboard limpo.";
    }

    private void StartNewNote()
    {
        SelectedNote = null;
        EditorTitle = string.Empty;
        EditorBody = string.Empty;
        EditorMetadataText = "Nova nota (será salva localmente e sincronizada conforme suas preferências).";
    }

    private async Task SaveNoteAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(EditorTitle))
        {
            StatusBanner = "Informe um título antes de salvar a nota.";
            return;
        }

        var res = SelectedNote is null
            ? await _notesService.CreateNoteAsync(EditorTitle, EditorBody, ct).ConfigureAwait(false)
            : await _notesService.UpdateNoteAsync(SelectedNote.NoteId, EditorTitle, EditorBody, ct).ConfigureAwait(false);

        if (res.IsFailure)
        {
            StatusBanner = $"Erro ao salvar nota: {res.Error?.Message}";
            return;
        }

        await RefreshNotesAndConflictsAsync(ct).ConfigureAwait(false);
        RunOnUi(() => SelectedNote = Notes.FirstOrDefault(n => n.NoteId == res.Value.NoteId));
        StatusBanner = $"Nota '{res.Value.Title}' salva (v{res.Value.Version}).";
    }

    private async Task DeleteSelectedNoteAsync(CancellationToken ct)
    {
        if (SelectedNote is null)
        {
            StatusBanner = "Selecione uma nota para excluir.";
            return;
        }

        var id = SelectedNote.NoteId;
        var res = await _notesService.DeleteNoteAsync(id, ct).ConfigureAwait(false);
        if (res.IsFailure)
        {
            StatusBanner = $"Erro ao excluir: {res.Error?.Message}";
            return;
        }

        RunOnUi(StartNewNote);
        await RefreshNotesAndConflictsAsync(ct).ConfigureAwait(false);
        StatusBanner = $"Nota marcada como excluída (tombstone v{res.Value.Version}).";
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
            StatusBanner = $"Erro ao restaurar versão: {res.Error?.Message}";
            return;
        }

        await RefreshNotesAndConflictsAsync(ct).ConfigureAwait(false);
        RunOnUi(() => SelectedNote = Notes.FirstOrDefault(n => n.NoteId == res.Value.NoteId));
        StatusBanner = $"Versão v{revision.Version} restaurada como v{res.Value.Version}.";
    }

    private async Task ResolveSelectedConflictAsync(ConflictResolutionChoice choice)
    {
        var conflict = SelectedConflict ?? UnresolvedConflicts.FirstOrDefault();
        if (conflict is null)
        {
            StatusBanner = "Nenhum conflito selecionado para resolver.";
            return;
        }

        var request = new ResolveConflictRequest
        {
            ConflictId = conflict.ConflictId,
            Choice = choice,
            MergedTitle = choice == ConflictResolutionChoice.ManualMerge ? ConflictMergedTitle : null,
            MergedBody = choice == ConflictResolutionChoice.ManualMerge ? ConflictMergedBody : null
        };

        var res = await _notesService.ResolveConflictAsync(request).ConfigureAwait(false);
        if (res.IsFailure)
        {
            StatusBanner = $"Erro ao resolver conflito: {res.Error?.Message}";
            return;
        }

        await RefreshNotesAndConflictsAsync(CancellationToken.None).ConfigureAwait(false);
        StatusBanner = $"Conflito resolvido ({choice}) -> Nota v{res.Value.Version}.";
    }

    private async Task RefreshNotesAndConflictsAsync(CancellationToken ct)
    {
        var notesRes = await _notesService.ListNotesAsync(SearchQuery, includeDeleted: false, ct).ConfigureAwait(false);
        var conflictsRes = await _notesService.ListConflictsAsync(unresolvedOnly: true, ct).ConfigureAwait(false);

        RunOnUi(() =>
        {
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
                        _ = LoadRevisionsForNoteAsync(match.NoteId);
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
        });
    }

    private async Task LoadRevisionsForNoteAsync(Guid noteId)
    {
        var revs = await _notesService.GetRevisionsAsync(noteId).ConfigureAwait(false);
        if (revs.IsSuccess)
        {
            RunOnUi(() => ReplaceCollection(SelectedNoteRevisions, revs.Value));
        }
    }

    private void ApplyOverview(SyncOverviewSnapshot overview)
    {
        SyncStatusText = overview.Status.ToString();
        LastSyncText = overview.LastSyncAtUtc.HasValue
            ? overview.LastSyncAtUtc.Value.ToLocalTime().ToString("g")
            : "Nunca sincronizado";
        PendingQueueText = $"{overview.PendingOperationsCount} na fila offline";
        ConflictsCountText = $"{overview.UnresolvedConflictsCount} conflito(s)";
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

    private Task RunOnUiAsync(Func<CancellationToken, Task> asyncAction)
    {
        if (_uiContext is not null && SynchronizationContext.Current != _uiContext)
        {
            var tcs = new TaskCompletionSource();
            _uiContext.Post(async _ =>
            {
                try
                {
                    await asyncAction(CancellationToken.None);
                    tcs.TrySetResult();
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }, null);
            return tcs.Task;
        }

        return asyncAction(CancellationToken.None);
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
