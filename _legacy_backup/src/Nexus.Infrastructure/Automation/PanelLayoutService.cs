using Microsoft.Extensions.Logging;
using Nexus.Contracts.Automation;
using Nexus.Core.Abstractions;
using Nexus.Core.Automation;
using Nexus.Core.Errors;

namespace Nexus.Infrastructure.Automation;

/// <summary>
/// Manages customizable Nexus Panel layouts, widget sizes, themes, accent colors,
/// multiple profiles, and automatic SQLite persistence on every layout mutation.
/// </summary>
public sealed class PanelLayoutService : IPanelLayoutService
{
    private readonly IPanelProfileRepository _repository;
    private readonly IClock _clock;
    private readonly ILogger<PanelLayoutService> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public PanelLayoutService(
        IPanelProfileRepository repository,
        IClock clock,
        ILogger<PanelLayoutService> logger)
    {
        _repository = repository;
        _clock = clock;
        _logger = logger;
    }

    public event EventHandler<PanelProfile>? ActiveProfileChanged;

    public async Task<Result<IReadOnlyList<PanelProfile>>> GetProfilesAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureSeededAsync(cancellationToken).ConfigureAwait(false);
            return await _repository.ListAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<Result<PanelProfile>> GetActiveProfileAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await GetActiveUnlockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<Result<PanelProfile>> SaveProfileAsync(PanelProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var errors = profile.Validate().ToArray();
        if (errors.Length > 0)
        {
            return Result<PanelProfile>.Failure(
                NexusError.Validation(errors[0].Message, code: errors[0].Code));
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var normalized = ReflowGrid(profile with
            {
                Revision = Math.Max(1, profile.Revision),
                UpdatedAtUtc = _clock.UtcNow
            });

            var res = await _repository.UpsertAsync(normalized, cancellationToken).ConfigureAwait(false);
            if (res.IsFailure)
            {
                return Result<PanelProfile>.Failure(res.Error!);
            }

            if (normalized.IsActive)
            {
                ActiveProfileChanged?.Invoke(this, normalized);
            }

            return Result<PanelProfile>.Success(normalized);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<Result<PanelProfile>> CreateProfileAsync(
        string name,
        PanelThemePreset theme = PanelThemePreset.MidnightNavy,
        PanelAccentColor accent = PanelAccentColor.Cyan,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Result<PanelProfile>.Failure(NexusError.Validation("Nome do perfil é obrigatório.", code: "PANEL_PROFILE_NAME_REQUIRED"));
        }

        var profile = new PanelProfile
        {
            ProfileId = Guid.NewGuid(),
            Name = name.Trim(),
            Theme = theme,
            Accent = accent,
            GridColumns = 2,
            IsActive = false,
            Widgets = BuildDefaultWidgets(),
            UpdatedAtUtc = _clock.UtcNow,
            Revision = 1
        };

        return await SaveProfileAsync(profile, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<PanelProfile>> ActivateProfileAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        PanelProfile activated;
        try
        {
            await EnsureSeededAsync(cancellationToken).ConfigureAwait(false);
            var existingRes = await _repository.GetByIdAsync(profileId, cancellationToken).ConfigureAwait(false);
            if (existingRes.IsFailure) return Result<PanelProfile>.Failure(existingRes.Error!);
            if (existingRes.Value is null)
            {
                return Result<PanelProfile>.Failure(NexusError.NotFound($"Perfil '{profileId:D}' não encontrado.", "PANEL_PROFILE_NOT_FOUND"));
            }

            activated = existingRes.Value with
            {
                IsActive = true,
                Revision = existingRes.Value.Revision + 1,
                UpdatedAtUtc = _clock.UtcNow
            };

            var saveRes = await _repository.UpsertAsync(activated, cancellationToken).ConfigureAwait(false);
            if (saveRes.IsFailure) return Result<PanelProfile>.Failure(saveRes.Error!);
        }
        finally
        {
            _lock.Release();
        }

        _logger.LogInformation("Activated Nexus Panel profile {ProfileId} ({Name})", activated.ProfileId, activated.Name);
        ActiveProfileChanged?.Invoke(this, activated);
        return Result<PanelProfile>.Success(activated);
    }

    public async Task<Result> DeleteProfileAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var allRes = await _repository.ListAsync(cancellationToken).ConfigureAwait(false);
            if (allRes.IsFailure) return Result.Failure(allRes.Error!);
            if (allRes.Value.Count <= 1)
            {
                return Result.Failure(NexusError.Validation("Não é possível excluir o único perfil restante do painel.", code: "PANEL_LAST_PROFILE"));
            }

            var delRes = await _repository.DeleteAsync(profileId, cancellationToken).ConfigureAwait(false);
            if (delRes.IsFailure) return delRes;

            var remaining = await _repository.ListAsync(cancellationToken).ConfigureAwait(false);
            if (remaining.IsSuccess && remaining.Value.Count > 0 && !remaining.Value.Any(p => p.IsActive))
            {
                var fallback = remaining.Value[0] with { IsActive = true, UpdatedAtUtc = _clock.UtcNow };
                await _repository.UpsertAsync(fallback, cancellationToken).ConfigureAwait(false);
                ActiveProfileChanged?.Invoke(this, fallback);
            }

            return Result.Success();
        }
        finally
        {
            _lock.Release();
        }
    }

    public Task<Result<PanelProfile>> AddWidgetAsync(
        PanelWidgetType widgetType,
        string? customTitle = null,
        PanelWidgetSize size = PanelWidgetSize.Compact1x1,
        CancellationToken cancellationToken = default)
    {
        return MutateActiveProfileAsync(active =>
        {
            var list = active.Widgets.ToList();
            list.Add(new PanelWidgetItem
            {
                WidgetId = Guid.NewGuid(),
                WidgetType = widgetType,
                Title = string.IsNullOrWhiteSpace(customTitle) ? DefaultWidgetTitle(widgetType) : customTitle.Trim(),
                Size = size,
                SortOrder = list.Count
            });
            return active with { Widgets = list };
        }, cancellationToken);
    }

    public Task<Result<PanelProfile>> RemoveWidgetAsync(Guid widgetId, CancellationToken cancellationToken = default)
    {
        return MutateActiveProfileAsync(active =>
        {
            var filtered = active.Widgets.Where(w => w.WidgetId != widgetId).ToList();
            if (filtered.Count == active.Widgets.Count)
            {
                throw new InvalidOperationException($"Widget '{widgetId:D}' não encontrado no perfil ativo.");
            }

            return active with { Widgets = filtered };
        }, cancellationToken);
    }

    public Task<Result<PanelProfile>> ResizeWidgetAsync(
        Guid widgetId,
        PanelWidgetSize newSize,
        CancellationToken cancellationToken = default)
    {
        return MutateActiveProfileAsync(active =>
        {
            var updated = active.Widgets
                .Select(w => w.WidgetId == widgetId ? w with { Size = newSize } : w)
                .ToList();

            if (!updated.Any(w => w.WidgetId == widgetId))
            {
                throw new InvalidOperationException($"Widget '{widgetId:D}' não encontrado no perfil ativo.");
            }

            return active with { Widgets = updated };
        }, cancellationToken);
    }

    public Task<Result<PanelProfile>> MoveWidgetAsync(
        Guid widgetId,
        int targetIndex,
        CancellationToken cancellationToken = default)
    {
        return MutateActiveProfileAsync(active =>
        {
            var ordered = active.Widgets.OrderBy(w => w.SortOrder).ToList();
            var oldIdx = ordered.FindIndex(w => w.WidgetId == widgetId);
            if (oldIdx < 0)
            {
                throw new InvalidOperationException($"Widget '{widgetId:D}' não encontrado no perfil ativo.");
            }

            var item = ordered[oldIdx];
            ordered.RemoveAt(oldIdx);
            var boundedTarget = Math.Clamp(targetIndex, 0, ordered.Count);
            ordered.Insert(boundedTarget, item);
            return active with { Widgets = ordered };
        }, cancellationToken);
    }

    public Task<Result<PanelProfile>> UpdateAppearanceAsync(
        PanelThemePreset theme,
        PanelAccentColor accent,
        int gridColumns,
        CancellationToken cancellationToken = default)
    {
        var boundedCols = Math.Clamp(gridColumns, 1, 4);
        return MutateActiveProfileAsync(active => active with
        {
            Theme = theme,
            Accent = accent,
            GridColumns = boundedCols
        }, cancellationToken);
    }

    private async Task<Result<PanelProfile>> MutateActiveProfileAsync(
        Func<PanelProfile, PanelProfile> transform,
        CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        PanelProfile saved;
        try
        {
            var activeRes = await GetActiveUnlockedAsync(cancellationToken).ConfigureAwait(false);
            if (activeRes.IsFailure) return activeRes;

            PanelProfile mutated;
            try
            {
                mutated = transform(activeRes.Value);
            }
            catch (InvalidOperationException ex)
            {
                return Result<PanelProfile>.Failure(NexusError.NotFound(ex.Message, "PANEL_WIDGET_NOT_FOUND"));
            }

            saved = ReflowGrid(mutated with
            {
                Revision = activeRes.Value.Revision + 1,
                UpdatedAtUtc = _clock.UtcNow
            });

            var upsert = await _repository.UpsertAsync(saved, cancellationToken).ConfigureAwait(false);
            if (upsert.IsFailure) return Result<PanelProfile>.Failure(upsert.Error!);
        }
        finally
        {
            _lock.Release();
        }

        ActiveProfileChanged?.Invoke(this, saved);
        return Result<PanelProfile>.Success(saved);
    }

    private async Task<Result<PanelProfile>> GetActiveUnlockedAsync(CancellationToken ct)
    {
        await EnsureSeededAsync(ct).ConfigureAwait(false);
        var active = await _repository.GetActiveAsync(ct).ConfigureAwait(false);
        if (active.IsFailure) return Result<PanelProfile>.Failure(active.Error!);
        if (active.Value is not null) return Result<PanelProfile>.Success(active.Value);

        var all = await _repository.ListAsync(ct).ConfigureAwait(false);
        if (all.IsFailure || all.Value.Count == 0)
        {
            return Result<PanelProfile>.Failure(NexusError.Unexpected("Nenhum perfil de painel disponível."));
        }

        var first = all.Value[0] with { IsActive = true, UpdatedAtUtc = _clock.UtcNow };
        await _repository.UpsertAsync(first, ct).ConfigureAwait(false);
        return Result<PanelProfile>.Success(first);
    }

    private async Task EnsureSeededAsync(CancellationToken ct)
    {
        var existing = await _repository.ListAsync(ct).ConfigureAwait(false);
        if (existing.IsFailure || existing.Value.Count > 0)
        {
            return;
        }

        var prod = ReflowGrid(new PanelProfile
        {
            ProfileId = Guid.NewGuid(),
            Name = "Produtividade",
            Theme = PanelThemePreset.MidnightNavy,
            Accent = PanelAccentColor.Cyan,
            GridColumns = 2,
            IsActive = true,
            Widgets = BuildDefaultWidgets(),
            UpdatedAtUtc = _clock.UtcNow,
            Revision = 1
        });

        var media = ReflowGrid(new PanelProfile
        {
            ProfileId = Guid.NewGuid(),
            Name = "Mídia & Controle",
            Theme = PanelThemePreset.OledBlack,
            Accent = PanelAccentColor.Violet,
            GridColumns = 2,
            IsActive = false,
            Widgets =
            [
                new() { WidgetType = PanelWidgetType.ConnectionStatus, Title = "Conexão", Size = PanelWidgetSize.Compact1x1, SortOrder = 0 },
                new() { WidgetType = PanelWidgetType.VolumeControl, Title = "Volume Master", Size = PanelWidgetSize.Compact1x1, SortOrder = 1 },
                new() { WidgetType = PanelWidgetType.MediaControls, Title = "Controles de Mídia", Size = PanelWidgetSize.Wide2x1, SortOrder = 2 },
                new() { WidgetType = PanelWidgetType.AppShortcuts, Title = "Apps & Atalhos", Size = PanelWidgetSize.Compact1x1, SortOrder = 3 },
                new() { WidgetType = PanelWidgetType.QuickActions, Title = "Ações Rápidas", Size = PanelWidgetSize.Compact1x1, SortOrder = 4 }
            ],
            UpdatedAtUtc = _clock.UtcNow,
            Revision = 1
        });

        await _repository.UpsertAsync(prod, ct).ConfigureAwait(false);
        await _repository.UpsertAsync(media, ct).ConfigureAwait(false);
    }

    private static PanelProfile ReflowGrid(PanelProfile profile)
    {
        var cols = Math.Clamp(profile.GridColumns, 1, 4);
        var placed = new List<PanelWidgetItem>(profile.Widgets.Count);
        var currentCol = 0;
        var currentRow = 0;

        for (var i = 0; i < profile.Widgets.Count; i++)
        {
            var widget = profile.Widgets[i];
            var span = Math.Min(widget.ColumnSpan, cols);
            if (currentCol + span > cols)
            {
                currentCol = 0;
                currentRow++;
            }

            placed.Add(widget with
            {
                SortOrder = i,
                GridColumn = currentCol,
                GridRow = currentRow
            });

            currentCol += span;
            if (currentCol >= cols)
            {
                currentCol = 0;
                currentRow += widget.RowSpan;
            }
        }

        return profile with { GridColumns = cols, Widgets = placed };
    }

    private static IReadOnlyList<PanelWidgetItem> BuildDefaultWidgets() =>
    [
        new() { WidgetType = PanelWidgetType.ConnectionStatus, Title = "Estado da Conexão", Size = PanelWidgetSize.Compact1x1, SortOrder = 0 },
        new() { WidgetType = PanelWidgetType.ComputerInfo, Title = "Computador Windows", Size = PanelWidgetSize.Compact1x1, SortOrder = 1 },
        new() { WidgetType = PanelWidgetType.CpuAndRam, Title = "CPU & RAM", Size = PanelWidgetSize.Wide2x1, SortOrder = 2 },
        new() { WidgetType = PanelWidgetType.VolumeControl, Title = "Volume", Size = PanelWidgetSize.Compact1x1, SortOrder = 3 },
        new() { WidgetType = PanelWidgetType.MediaControls, Title = "Mídia", Size = PanelWidgetSize.Compact1x1, SortOrder = 4 },
        new() { WidgetType = PanelWidgetType.TransferStatus, Title = "Transferências", Size = PanelWidgetSize.Compact1x1, SortOrder = 5 },
        new() { WidgetType = PanelWidgetType.RecentNotes, Title = "Notas Recentes", Size = PanelWidgetSize.Compact1x1, SortOrder = 6 },
        new() { WidgetType = PanelWidgetType.NetworkInfo, Title = "Rede", Size = PanelWidgetSize.Wide2x1, SortOrder = 7 }
    ];

    public static string DefaultWidgetTitle(PanelWidgetType type) => type switch
    {
        PanelWidgetType.ConnectionStatus => "Estado da Conexão",
        PanelWidgetType.ComputerInfo => "Informações do Computador",
        PanelWidgetType.CpuAndRam => "CPU e RAM",
        PanelWidgetType.VolumeControl => "Controle de Volume",
        PanelWidgetType.MediaControls => "Controles de Mídia",
        PanelWidgetType.AppShortcuts => "Atalhos de Aplicativos",
        PanelWidgetType.QuickActions => "Ações Rápidas",
        PanelWidgetType.TransferStatus => "Estado das Transferências",
        PanelWidgetType.RecentNotes => "Notas Recentes",
        PanelWidgetType.NetworkInfo => "Informações de Rede",
        _ => type.ToString()
    };
}

