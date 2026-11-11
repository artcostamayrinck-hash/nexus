using System.Globalization;
using Nexus.Contracts.Transfers;

namespace Nexus.Core.Transfers;

/// <summary>
/// Presentation projection and shared formatting helpers for UI transfer views on Desktop and Mobile.
/// </summary>
public sealed record TransferDisplayItem
{
    public required Guid TransferId { get; init; }
    public required string FileName { get; init; }
    public required string DirectionDisplay { get; init; }
    public required string StageDisplay { get; init; }
    public required string PeerDeviceName { get; init; }
    public required string SizeDisplay { get; init; }
    public required string TransferredDisplay { get; init; }
    public required string SpeedDisplay { get; init; }
    public required string ProgressSummary { get; init; }
    public required double PercentComplete { get; init; }
    public double ProgressRatio => Math.Clamp(PercentComplete / 100d, 0d, 1d);
    public required TransferDirection Direction { get; init; }
    public required TransferState State { get; init; }
    public required TransferStage Stage { get; init; }
    public required TransferFailureReason FailureReason { get; init; }
    public string? FailureMessage { get; init; }
    public string? LocalFilePath { get; init; }
    public string ChecksumShort { get; init; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset? CompletedAtUtc { get; init; }

    public bool CanPause => State is TransferState.Pending or TransferState.InProgress
        && Stage is not TransferStage.ReadyForDownload;

    public bool CanResume => State is TransferState.Paused
        || (State == TransferState.Failed && FailureReason == TransferFailureReason.NetworkInterrupted);

    public bool CanCancel => State is TransferState.Pending or TransferState.InProgress or TransferState.Paused or TransferState.Failed;

    public bool IsCompletedIncoming => Direction == TransferDirection.Download && State == TransferState.Completed;

    public string TimeDisplay => (CompletedAtUtc ?? CreatedAtUtc).ToLocalTime().ToString("dd/MM HH:mm:ss", CultureInfo.CurrentCulture);

    public static TransferDisplayItem FromRecord(FileTransferRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var sizeText = FormatBytes(record.TotalBytes);
        var transferredText = FormatBytes(record.TransferredBytes);
        var speedText = record.BytesPerSecond > 0.5 && record.State == TransferState.InProgress
            ? $"{FormatBytes((long)record.BytesPerSecond)}/s"
            : "-";

        var peerName = record.Direction == TransferDirection.Upload
            ? record.TargetDeviceName
            : record.SourceDeviceName;

        var checksumShort = record.Sha256Checksum.Length >= 12
            ? record.Sha256Checksum[..12] + "…"
            : record.Sha256Checksum;

        return new TransferDisplayItem
        {
            TransferId = record.TransferId,
            FileName = record.SafeFileName,
            Direction = record.Direction,
            DirectionDisplay = record.Direction == TransferDirection.Upload ? "Enviando" : "Recebendo",
            Stage = record.Stage,
            StageDisplay = FormatStage(record),
            PeerDeviceName = peerName,
            SizeDisplay = sizeText,
            TransferredDisplay = transferredText,
            SpeedDisplay = speedText,
            PercentComplete = record.PercentComplete,
            ProgressSummary = $"{record.PercentComplete:F1}% · {transferredText} / {sizeText} · {speedText}",
            State = record.State,
            FailureReason = record.FailureReason,
            FailureMessage = record.FailureMessage,
            LocalFilePath = record.LocalFilePath,
            ChecksumShort = checksumShort,
            CreatedAtUtc = record.CreatedAtUtc,
            CompletedAtUtc = record.CompletedAtUtc
        };
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        if (bytes < 1024L * 1024)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024d:F1} KB");
        }

        if (bytes < 1024L * 1024 * 1024)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024d * 1024):F2} MB");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024d * 1024 * 1024):F2} GB");
    }

    private static string FormatStage(FileTransferRecord record)
    {
        if (record.FailureReason == TransferFailureReason.DuplicateSkipped && record.State == TransferState.Completed)
        {
            return "Ignorado (duplicado)";
        }

        return record.Stage switch
        {
            TransferStage.Queued => "Na fila",
            TransferStage.Uploading => "Enviando (HTTP)",
            TransferStage.ReadyForDownload => record.Direction == TransferDirection.Upload
                ? "Aguardando destinatário"
                : "Pronto para baixar",
            TransferStage.Downloading => "Baixando (HTTP)",
            TransferStage.Verifying => "Verificando SHA-256",
            TransferStage.Completed => "Concluído",
            TransferStage.Paused => "Pausado",
            TransferStage.Failed => "Falha",
            TransferStage.Cancelled => "Cancelado",
            _ => record.State.ToString()
        };
    }
}
