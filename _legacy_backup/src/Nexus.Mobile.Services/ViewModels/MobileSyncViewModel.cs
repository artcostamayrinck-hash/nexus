using System.Collections.ObjectModel;
using Nexus.Contracts.Sync;
using Nexus.Core.Sync;
using Nexus.Mobile.Services.Mvvm;

namespace Nexus.Mobile.Services.ViewModels;

public sealed class MobileSyncViewModel : MobileObservableObject
{
    private readonly IClipboardSyncService _clipboardService;
    private readonly INotesSyncService _notesService;
    private readonly ISyncOrchestrator _syncOrchestrator;
    private readonly ISyncPrivacySettingsService _privacyService;
    private readonly ISystemClipboardAdapter _clipboardAdapter;
    private readonly SynchronizationContext? _uiContext;

    private string _syncStatusText = "Ocioso";
    private string _lastSyncText = "Nunca sincronizado";
    private string _pendingQueueText = "0 na fila";
    private string _conflictsCountText = "0 conflito(s)";
    private string _platformRestrictionText;
    private string _statusMessage = "Sincronização NEXUS pronta.";

    private bool _clipboardSyncEnabled = true;
    private bool _clipboardAutoSync = true;
    private int _clipboardMaxHistoryItems = 50;
    private bool _notesSyncEnabled = true;
    private bool _notesAutoSync = true;

    private string _clipboardDraftText = string.Empty;
    private string _searchQuery = string.Empty;
    private NexusNoteContract? _selectedNote;
    private string _noteTitleInput = string.Empty;
    private string _noteBodyInput = string.Empty;
    private SyncConflictRecord? _selectedConflict;
    private string _mergedTitle = string.Empty;
    private string _mergedBody = string.Empty;

    public MobileSyncViewModel(
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
        _platformRestrictionText = clipboardAdapter.PlatformRestrictionNotice;

        SyncNowCommand = new MobileAsyncCommand(SyncNowAsync);
        SavePrivacyCommand = new MobileAsyncCommand(SavePrivacyAsync);
        SendClipboardDraftCommand = new MobileAsyncCommand(SendClipboardDraftAsync);
        CaptureForegroundClipboardCommand = new MobileAsyncCommand(CaptureForegroundClipboardAsync);
        CopyEntryCommand = new MobileAsyncCommand<ClipboardEntryContract>(CopyEntryAsync);
        ClearClipboardCommand = new MobileAsyncCommand(ClearClipboardAsync);
        NewNoteCommand = new MobileAsyncCommand(_ =>
        {
            RunOnUi(ResetEditor);
            return Task.CompletedTask;
        });
        SaveNoteCommand = new MobileAsyncCommand(SaveNoteAsync);
        DeleteSelectedNoteCommand = new MobileAsyncCommand(DeleteSelectedNoteAsync);
        RestoreRevisionCommand = new MobileAsyncCommand<NoteRevisionSnapshot>(RestoreRevisionAsync);
        KeepLocalConflictCommand = new MobileAsyncCommand(_ => ResolveConflictAsync(ConflictResolutionChoice.KeepLocal));
        AcceptRemoteConflictCommand = new MobileAsyncCommand(_ => ResolveConflictAsync(ConflictResolutionChoice.AcceptRemote));
        ManualMergeConflictCommand = new MobileAsyncCommand(_ => ResolveConflictAsync(ConflictResolutionChoice.ManualMerge));

        _syncOrchestrator.OverviewChanged += (_, overview) => RunOnUi(() => ApplyOverview(overview));
        _clipboardService.HistoryChanged += (_, history) => RunOnUi(() => Replace(ClipboardHistory, history));
        _notesService.NotesOrConflictsChanged += (_, _) => _ = RefreshNotesAndConflictsAsync(CancellationToken.None);
    }

    public ObservableCollection<ClipboardEntryContract> ClipboardHistory { get; } = new();
    public ObservableCollection<NexusNoteContract> Notes { get; } = new();
    public ObservableCollection<NoteRevisionSnapshot> Revisions { get; } = new();
    public ObservableCollection<SyncConflictRecord> Conflicts { get; } = new();

    public string SyncStatusText { get => _syncStatusText; private set => SetProperty(ref _syncStatusText, value); }
    public string LastSyncText { get => _lastSyncText; private set => SetProperty(ref _lastSyncText, value); }
    public string PendingQueueText { get => _pendingQueueText; private set => SetProperty(ref _pendingQueueText, value); }
    public string ConflictsCountText { get => _conflictsCountText; private set => SetProperty(ref _conflictsCountText, value); }
    public string PlatformRestrictionText { get => _platformRestrictionText; private set => SetProperty(ref _platformRestrictionText, value); }
    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }

    public bool ClipboardSyncEnabled { get => _clipboardSyncEnabled; set => SetProperty(ref _clipboardSyncEnabled, value); }
    public bool ClipboardAutoSync { get => _clipboardAutoSync; set => SetProperty(ref _clipboardAutoSync, value); }
    public int ClipboardMaxHistoryItems { get => _clipboardMaxHistoryItems; set => SetProperty(ref _clipboardMaxHistoryItems, value); }
    public bool NotesSyncEnabled { get => _notesSyncEnabled; set => SetProperty(ref _notesSyncEnabled, value); }
    public bool NotesAutoSync { get => _notesAutoSync; set => SetProperty(ref _notesAutoSync, value); }

    public string ClipboardDraftText { get => _clipboardDraftText; set => SetProperty(ref _clipboardDraftText, value); }

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
            if (SetProperty(ref _selectedNote, value) && value is not null)
            {
                NoteTitleInput = value.Title;
                NoteBodyInput = value.Body;
                _ = LoadRevisionsAsync(value.NoteId);
            }
        }
    }

    public string NoteTitleInput { get => _noteTitleInput; set => SetProperty(ref _noteTitleInput, value); }
    public string NoteBodyInput { get => _noteBodyInput; set => SetProperty(ref _noteBodyInput, value); }

    public SyncConflictRecord? SelectedConflict
    {
        get => _selectedConflict;
        set
        {
            if (SetProperty(ref _selectedConflict, value) && value is not null)
            {
                MergedTitle = value.LocalTitle;
                MergedBody = $"{value.LocalBody}\n---\n{value.RemoteBody}";
            }
        }
    }

    public string MergedTitle { get => _mergedTitle; set => SetProperty(ref _mergedTitle, value); }
    public string MergedBody { get => _mergedBody; set => SetProperty(ref _mergedBody, value); }

    public MobileAsyncCommand SyncNowCommand { get; }
    public MobileAsyncCommand SavePrivacyCommand { get; }
    public MobileAsyncCommand SendClipboardDraftCommand { get; }
    public MobileAsyncCommand CaptureForegroundClipboardCommand { get; }
    public MobileAsyncCommand<ClipboardEntryContract> CopyEntryCommand { get; }
    public MobileAsyncCommand ClearClipboardCommand { get; }
    public MobileAsyncCommand NewNoteCommand { get; }
    public MobileAsyncCommand SaveNoteCommand { get; }
    public MobileAsyncCommand DeleteSelectedNoteCommand { get; }
    public MobileAsyncCommand<NoteRevisionSnapshot> RestoreRevisionCommand { get; }
    public MobileAsyncCommand KeepLocalConflictCommand { get; }
    public MobileAsyncCommand AcceptRemoteConflictCommand { get; }
    public MobileAsyncCommand ManualMergeConflictCommand { get; }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var prefsRes = await _privacyService.GetPreferencesAsync(ct).ConfigureAwait(false);
        if (prefsRes.IsSuccess)
        {
            var p = prefsRes.Value;
            RunOnUi(() =>
            {
                ClipboardSyncEnabled = p.ClipboardSyncEnabled;
                ClipboardAutoSync = p.ClipboardAutoSync;
                ClipboardMaxHistoryItems = p.ClipboardMaxHistoryItems;
                NotesSyncEnabled = p.NotesSyncEnabled;
                NotesAutoSync = p.NotesAutoSync;
            });
        }

        var overviewRes = await _syncOrchestrator.InitializeAsync(ct).ConfigureAwait(false);
        if (overviewRes.IsSuccess)
        {
            RunOnUi(() => ApplyOverview(overviewRes.Value));
        }

        var histRes = await _clipboardService.GetHistoryAsync(ct).ConfigureAwait(false);
        if (histRes.IsSuccess)
        {
            RunOnUi(() => Replace(ClipboardHistory, histRes.Value));
        }

        await RefreshNotesAndConflictsAsync(ct).ConfigureAwait(false);
    }

    private async Task SyncNowAsync(CancellationToken ct)
    {
        var res = await _syncOrchestrator.SyncNowAsync(ct).ConfigureAwait(false);
        if (res.IsSuccess)
        {
            RunOnUi(() => ApplyOverview(res.Value));
            await RefreshNotesAndConflictsAsync(ct).ConfigureAwait(false);
            var hist = await _clipboardService.GetHistoryAsync(ct).ConfigureAwait(false);
            if (hist.IsSuccess)
            {
                RunOnUi(() => Replace(ClipboardHistory, hist.Value));
            }
            StatusMessage = res.Value.LastDiagnostic ?? "Sincronização concluída.";
        }
        else
        {
            StatusMessage = $"Erro ao sincronizar: {res.Error?.Message}";
        }
    }

    private async Task SavePrivacyAsync(CancellationToken ct)
    {
        var prefs = new SyncPrivacyPreferences
        {
            ClipboardSyncEnabled = ClipboardSyncEnabled,
            ClipboardAutoSync = ClipboardAutoSync,
            ClipboardMaxHistoryItems = Math.Clamp(ClipboardMaxHistoryItems, 1, 500),
            NotesSyncEnabled = NotesSyncEnabled,
            NotesAutoSync = NotesAutoSync
        };

        var saved = await _privacyService.SavePreferencesAsync(prefs, ct).ConfigureAwait(false);
        StatusMessage = saved.IsSuccess
            ? "Preferências de privacidade salvas."
            : $"Erro ao salvar: {saved.Error?.Message}";
    }

    private async Task SendClipboardDraftAsync(CancellationToken ct)
    {
        var res = await _clipboardService.CaptureAndSendTextAsync(ClipboardDraftText, manualTrigger: true, ct).ConfigureAwait(false);
        if (res.IsSuccess)
        {
            RunOnUi(() => ClipboardDraftText = string.Empty);
            StatusMessage = "Texto enviado para o Clipboard NEXUS.";
        }
        else
        {
            StatusMessage = res.Error?.Message ?? "Erro ao enviar clipboard.";
        }
    }

    private async Task CaptureForegroundClipboardAsync(CancellationToken ct)
    {
        var res = await _clipboardService.CaptureCurrentOsClipboardAsync(ct).ConfigureAwait(false);
        if (res.IsFailure)
        {
            StatusMessage = res.Error?.Message ?? "Falha ao capturar clipboard.";
            return;
        }

        StatusMessage = res.Value is null
            ? "Clipboard do Android vazio."
            : "Clipboard capturado em primeiro plano e enviado.";
    }

    private async Task CopyEntryAsync(ClipboardEntryContract? entry, CancellationToken ct)
    {
        if (entry is null)
        {
            return;
        }

        var write = await _clipboardAdapter.WriteTextAsync(entry.ContentText, ct).ConfigureAwait(false);
        StatusMessage = write.IsSuccess
            ? "Copiado para o clipboard do Android."
            : $"Erro ao copiar: {write.Error?.Message}";
    }

    private async Task ClearClipboardAsync(CancellationToken ct)
    {
        await _clipboardService.ClearHistoryAsync(ct).ConfigureAwait(false);
        StatusMessage = "Histórico local limpo.";
    }

    private void ResetEditor()
    {
        SelectedNote = null;
        NoteTitleInput = string.Empty;
        NoteBodyInput = string.Empty;
        Revisions.Clear();
    }

    private async Task SaveNoteAsync(CancellationToken ct)
    {
        var res = SelectedNote is null
            ? await _notesService.CreateNoteAsync(NoteTitleInput, NoteBodyInput, ct).ConfigureAwait(false)
            : await _notesService.UpdateNoteAsync(SelectedNote.NoteId, NoteTitleInput, NoteBodyInput, ct).ConfigureAwait(false);

        if (res.IsFailure)
        {
            StatusMessage = $"Erro na nota: {res.Error?.Message}";
            return;
        }

        await RefreshNotesAndConflictsAsync(ct).ConfigureAwait(false);
        RunOnUi(() => SelectedNote = Notes.FirstOrDefault(n => n.NoteId == res.Value.NoteId));
        StatusMessage = $"Nota '{res.Value.Title}' salva (v{res.Value.Version}).";
    }

    private async Task DeleteSelectedNoteAsync(CancellationToken ct)
    {
        if (SelectedNote is null)
        {
            return;
        }

        var res = await _notesService.DeleteNoteAsync(SelectedNote.NoteId, ct).ConfigureAwait(false);
        if (res.IsSuccess)
        {
            RunOnUi(ResetEditor);
            await RefreshNotesAndConflictsAsync(ct).ConfigureAwait(false);
            StatusMessage = "Nota excluída.";
        }
    }

    private async Task RestoreRevisionAsync(NoteRevisionSnapshot? rev, CancellationToken ct)
    {
        if (rev is null)
        {
            return;
        }

        var res = await _notesService.RestoreRevisionAsync(rev.NoteId, rev.RevisionId, ct).ConfigureAwait(false);
        if (res.IsSuccess)
        {
            await RefreshNotesAndConflictsAsync(ct).ConfigureAwait(false);
            StatusMessage = $"Revisão v{rev.Version} restaurada.";
        }
    }

    private async Task ResolveConflictAsync(ConflictResolutionChoice choice)
    {
        var conflict = SelectedConflict ?? Conflicts.FirstOrDefault();
        if (conflict is null)
        {
            return;
        }

        var req = new ResolveConflictRequest
        {
            ConflictId = conflict.ConflictId,
            Choice = choice,
            MergedTitle = choice == ConflictResolutionChoice.ManualMerge ? MergedTitle : null,
            MergedBody = choice == ConflictResolutionChoice.ManualMerge ? MergedBody : null
        };

        var res = await _notesService.ResolveConflictAsync(req).ConfigureAwait(false);
        if (res.IsSuccess)
        {
            await RefreshNotesAndConflictsAsync(CancellationToken.None).ConfigureAwait(false);
            StatusMessage = $"Conflito resolvido ({choice}).";
        }
    }

    private async Task RefreshNotesAndConflictsAsync(CancellationToken ct)
    {
        var notes = await _notesService.ListNotesAsync(SearchQuery, includeDeleted: false, ct).ConfigureAwait(false);
        var conflicts = await _notesService.ListConflictsAsync(unresolvedOnly: true, ct).ConfigureAwait(false);

        RunOnUi(() =>
        {
            if (notes.IsSuccess)
            {
                Replace(Notes, notes.Value);
            }

            if (conflicts.IsSuccess)
            {
                Replace(Conflicts, conflicts.Value);
                if (SelectedConflict is null || !Conflicts.Any(c => c.ConflictId == SelectedConflict.ConflictId))
                {
                    SelectedConflict = Conflicts.FirstOrDefault();
                }
            }
        });
    }

    private async Task LoadRevisionsAsync(Guid noteId)
    {
        var revs = await _notesService.GetRevisionsAsync(noteId).ConfigureAwait(false);
        if (revs.IsSuccess)
        {
            RunOnUi(() => Replace(Revisions, revs.Value));
        }
    }

    private void ApplyOverview(SyncOverviewSnapshot overview)
    {
        SyncStatusText = overview.Status.ToString();
        LastSyncText = overview.LastSyncAtUtc.HasValue
            ? overview.LastSyncAtUtc.Value.ToLocalTime().ToString("g")
            : "Nunca sincronizado";
        PendingQueueText = $"{overview.PendingOperationsCount} na fila";
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

    private static void Replace<T>(ObservableCollection<T> target, IReadOnlyList<T> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }
}

