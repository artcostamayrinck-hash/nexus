using System.ComponentModel.DataAnnotations;
using Nexus.Contracts.Common;

namespace Nexus.Contracts.Sync;

public enum SyncDirection
{
    Push = 0,
    Pull = 1,
    Bidirectional = 2
}

public enum SyncStatus
{
    Idle = 0,
    Syncing = 1,
    Completed = 2,
    Conflict = 3,
    Failed = 4
}

public enum ConflictResolutionPolicy
{
    LastWriteWins = 0,
    PreferLocal = 1,
    PreferRemote = 2,
    Manual = 3
}

public sealed record SyncItemMetadata(
    [property: Required, StringLength(100, MinimumLength = 1)] string EntityType,
    [property: Required, StringLength(200, MinimumLength = 1)] string EntityKey,
    [property: Range(0, long.MaxValue)] long Revision,
    [property: Required, StringLength(128, MinimumLength = 1)] string ContentHash,
    DateTimeOffset ModifiedAtUtc,
    bool IsDeleted = false);

public sealed record SyncBatchContract : IVersionedContract, IValidatableContract
{
    public Guid SyncId { get; init; } = Guid.NewGuid();
    public Guid SourceDeviceId { get; init; }
    public Guid TargetDeviceId { get; init; }

    [Required]
    [StringLength(100, MinimumLength = 1)]
    public string Scope { get; init; } = string.Empty;

    public SyncDirection Direction { get; init; } = SyncDirection.Bidirectional;
    public SyncStatus Status { get; init; } = SyncStatus.Idle;
    public ConflictResolutionPolicy ConflictPolicy { get; init; } = ConflictResolutionPolicy.LastWriteWins;

    [Range(0, long.MaxValue)]
    public long BaseRevision { get; init; }

    [Range(0, long.MaxValue)]
    public long TargetRevision { get; init; }

    public IReadOnlyList<SyncItemMetadata> Items { get; init; } = Array.Empty<SyncItemMetadata>();
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string ContractVersion { get; init; } = ContractVersions.Current;

    public IEnumerable<ContractValidationError> Validate()
    {
        if (SyncId == Guid.Empty)
        {
            yield return new ContractValidationError(nameof(SyncId), "SYNC_ID_EMPTY", "SyncId cannot be empty.");
        }

        if (SourceDeviceId == Guid.Empty || TargetDeviceId == Guid.Empty)
        {
            yield return new ContractValidationError(nameof(SourceDeviceId), "SYNC_DEVICE_ID_EMPTY", "Source and Target device IDs are required.");
        }

        if (TargetRevision < BaseRevision)
        {
            yield return new ContractValidationError(nameof(TargetRevision), "SYNC_REVISION_REGRESSION", "TargetRevision cannot be less than BaseRevision.");
        }

        for (var i = 0; i < Items.Count; i++)
        {
            var item = Items[i];
            if (string.IsNullOrWhiteSpace(item.EntityType) || string.IsNullOrWhiteSpace(item.EntityKey) || string.IsNullOrWhiteSpace(item.ContentHash))
            {
                yield return new ContractValidationError($"{nameof(Items)}[{i}]", "SYNC_ITEM_INVALID", "Sync item must specify EntityType, EntityKey, and ContentHash.");
            }
        }
    }
}

