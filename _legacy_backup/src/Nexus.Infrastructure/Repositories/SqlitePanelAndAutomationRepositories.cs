using System.Globalization;
using Microsoft.Data.Sqlite;
using Nexus.Contracts.Automation;
using Nexus.Core.Abstractions;
using Nexus.Core.Automation;
using Nexus.Core.Errors;
using Nexus.Infrastructure.Persistence;

namespace Nexus.Infrastructure.Repositories;

/// <summary>
/// SQLite repository for NEXUS Panel profiles and widget grid layouts.
/// </summary>
public sealed class SqlitePanelProfileRepository : IPanelProfileRepository
{
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly StorageFailureHandler _failureHandler;
    private readonly IJsonSerializer _serializer;

    public SqlitePanelProfileRepository(
        ISqliteConnectionFactory connectionFactory,
        StorageFailureHandler failureHandler,
        IJsonSerializer serializer)
    {
        _connectionFactory = connectionFactory;
        _failureHandler = failureHandler;
        _serializer = serializer;
    }

    public Task<Result<IReadOnlyList<PanelProfile>>> ListAsync(CancellationToken cancellationToken = default)
    {
        return _failureHandler.ExecuteAsync<IReadOnlyList<PanelProfile>>("PanelProfiles.List", async ct =>
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT PayloadJson FROM PanelProfiles ORDER BY IsActive DESC, Name ASC;";

            var items = new List<PanelProfile>();
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var profile = _serializer.Deserialize<PanelProfile>(reader.GetString(0));
                if (profile is not null)
                {
                    items.Add(profile);
                }
            }

            return items.AsReadOnly();
        }, cancellationToken);
    }

    public Task<Result<PanelProfile?>> GetByIdAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        return _failureHandler.ExecuteAsync<PanelProfile?>("PanelProfiles.GetById", async ct =>
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT PayloadJson FROM PanelProfiles WHERE ProfileId = $id;";
            cmd.Parameters.AddWithValue("$id", profileId.ToString("D"));

            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            return await reader.ReadAsync(ct).ConfigureAwait(false)
                ? _serializer.Deserialize<PanelProfile>(reader.GetString(0))
                : null;
        }, cancellationToken);
    }

    public Task<Result<PanelProfile?>> GetActiveAsync(CancellationToken cancellationToken = default)
    {
        return _failureHandler.ExecuteAsync<PanelProfile?>("PanelProfiles.GetActive", async ct =>
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT PayloadJson FROM PanelProfiles WHERE IsActive = 1 ORDER BY UpdatedAtUtc DESC LIMIT 1;";

            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            return await reader.ReadAsync(ct).ConfigureAwait(false)
                ? _serializer.Deserialize<PanelProfile>(reader.GetString(0))
                : null;
        }, cancellationToken);
    }

    public Task<Result> UpsertAsync(PanelProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return _failureHandler.ExecuteAsync("PanelProfiles.Upsert", async ct =>
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

            if (profile.IsActive)
            {
                await using var deactivateCmd = conn.CreateCommand();
                deactivateCmd.Transaction = (SqliteTransaction)tx;
                deactivateCmd.CommandText = "UPDATE PanelProfiles SET IsActive = 0 WHERE ProfileId <> $id;";
                deactivateCmd.Parameters.AddWithValue("$id", profile.ProfileId.ToString("D"));
                await deactivateCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

                // Keep PayloadJson aligned on deactivated rows so ListAsync reads accurate IsActive flags
                await using var activeRowsCmd = conn.CreateCommand();
                activeRowsCmd.Transaction = (SqliteTransaction)tx;
                activeRowsCmd.CommandText = "SELECT ProfileId, PayloadJson FROM PanelProfiles WHERE ProfileId <> $id;";
                activeRowsCmd.Parameters.AddWithValue("$id", profile.ProfileId.ToString("D"));

                var toUpdate = new List<(string Id, string Json)>();
                await using (var reader = await activeRowsCmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        var parsed = _serializer.Deserialize<PanelProfile>(reader.GetString(1));
                        if (parsed is not null && parsed.IsActive)
                        {
                            toUpdate.Add((reader.GetString(0), _serializer.Serialize(parsed with { IsActive = false })));
                        }
                    }
                }

                foreach (var (otherId, json) in toUpdate)
                {
                    await using var fixCmd = conn.CreateCommand();
                    fixCmd.Transaction = (SqliteTransaction)tx;
                    fixCmd.CommandText = "UPDATE PanelProfiles SET PayloadJson = $json, IsActive = 0 WHERE ProfileId = $id;";
                    fixCmd.Parameters.AddWithValue("$id", otherId);
                    fixCmd.Parameters.AddWithValue("$json", json);
                    await fixCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
            }

            await using var cmd = conn.CreateCommand();
            cmd.Transaction = (SqliteTransaction)tx;
            cmd.CommandText = """
                INSERT INTO PanelProfiles (
                    ProfileId, Name, IsActive, Theme, Accent, GridColumns, Revision, PayloadJson, UpdatedAtUtc, ContractVersion
                )
                VALUES ($id, $name, $isActive, $theme, $accent, $cols, $rev, $json, $updatedAt, $version)
                ON CONFLICT(ProfileId) DO UPDATE SET
                    Name = excluded.Name,
                    IsActive = excluded.IsActive,
                    Theme = excluded.Theme,
                    Accent = excluded.Accent,
                    GridColumns = excluded.GridColumns,
                    Revision = excluded.Revision,
                    PayloadJson = excluded.PayloadJson,
                    UpdatedAtUtc = excluded.UpdatedAtUtc;
                """;
            cmd.Parameters.AddWithValue("$id", profile.ProfileId.ToString("D"));
            cmd.Parameters.AddWithValue("$name", profile.Name);
            cmd.Parameters.AddWithValue("$isActive", profile.IsActive ? 1 : 0);
            cmd.Parameters.AddWithValue("$theme", (int)profile.Theme);
            cmd.Parameters.AddWithValue("$accent", (int)profile.Accent);
            cmd.Parameters.AddWithValue("$cols", profile.GridColumns);
            cmd.Parameters.AddWithValue("$rev", profile.Revision);
            cmd.Parameters.AddWithValue("$json", _serializer.Serialize(profile));
            cmd.Parameters.AddWithValue("$updatedAt", profile.UpdatedAtUtc.ToString("O"));
            cmd.Parameters.AddWithValue("$version", profile.ContractVersion);

            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }, cancellationToken);
    }

    public async Task<Result> DeleteAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        var affected = await _failureHandler.ExecuteAsync<int>("PanelProfiles.Delete", async ct =>
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM PanelProfiles WHERE ProfileId = $id;";
            cmd.Parameters.AddWithValue("$id", profileId.ToString("D"));
            return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        if (affected.IsFailure) return Result.Failure(affected.Error!);
        return affected.Value > 0
            ? Result.Success()
            : Result.Failure(NexusError.NotFound($"Perfil '{profileId:D}' não encontrado.", "PANEL_PROFILE_NOT_FOUND"));
    }
}

/// <summary>
/// SQLite repository for Nexus Automate rule definitions.
/// </summary>
public sealed class SqliteAutomationRuleRepository : IAutomationRuleRepository
{
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly StorageFailureHandler _failureHandler;
    private readonly IJsonSerializer _serializer;

    public SqliteAutomationRuleRepository(
        ISqliteConnectionFactory connectionFactory,
        StorageFailureHandler failureHandler,
        IJsonSerializer serializer)
    {
        _connectionFactory = connectionFactory;
        _failureHandler = failureHandler;
        _serializer = serializer;
    }

    public Task<Result<IReadOnlyList<AutomationRule>>> ListAsync(CancellationToken cancellationToken = default)
    {
        return _failureHandler.ExecuteAsync<IReadOnlyList<AutomationRule>>("AutomationRules.List", async ct =>
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT PayloadJson FROM AutomationRules ORDER BY Name ASC;";

            var items = new List<AutomationRule>();
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var rule = _serializer.Deserialize<AutomationRule>(reader.GetString(0));
                if (rule is not null)
                {
                    items.Add(rule);
                }
            }

            return items.AsReadOnly();
        }, cancellationToken);
    }

    public Task<Result<AutomationRule?>> GetByIdAsync(Guid ruleId, CancellationToken cancellationToken = default)
    {
        return _failureHandler.ExecuteAsync<AutomationRule?>("AutomationRules.GetById", async ct =>
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT PayloadJson FROM AutomationRules WHERE RuleId = $id;";
            cmd.Parameters.AddWithValue("$id", ruleId.ToString("D"));

            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            return await reader.ReadAsync(ct).ConfigureAwait(false)
                ? _serializer.Deserialize<AutomationRule>(reader.GetString(0))
                : null;
        }, cancellationToken);
    }

    public Task<Result> UpsertAsync(AutomationRule rule, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rule);

        return _failureHandler.ExecuteAsync("AutomationRules.Upsert", async ct =>
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO AutomationRules (
                    RuleId, Name, IsEnabled, TriggerType, PayloadJson, LastExecutedAtUtc, UpdatedAtUtc, ContractVersion
                )
                VALUES ($id, $name, $enabled, $trigger, $json, $lastExec, $updatedAt, $version)
                ON CONFLICT(RuleId) DO UPDATE SET
                    Name = excluded.Name,
                    IsEnabled = excluded.IsEnabled,
                    TriggerType = excluded.TriggerType,
                    PayloadJson = excluded.PayloadJson,
                    LastExecutedAtUtc = excluded.LastExecutedAtUtc,
                    UpdatedAtUtc = excluded.UpdatedAtUtc;
                """;
            cmd.Parameters.AddWithValue("$id", rule.RuleId.ToString("D"));
            cmd.Parameters.AddWithValue("$name", rule.Name);
            cmd.Parameters.AddWithValue("$enabled", rule.IsEnabled ? 1 : 0);
            cmd.Parameters.AddWithValue("$trigger", (int)rule.Trigger);
            cmd.Parameters.AddWithValue("$json", _serializer.Serialize(rule));
            cmd.Parameters.AddWithValue("$lastExec", rule.LastExecutedAtUtc?.ToString("O") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$updatedAt", rule.UpdatedAtUtc.ToString("O"));
            cmd.Parameters.AddWithValue("$version", rule.ContractVersion);

            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, cancellationToken);
    }

    public async Task<Result> DeleteAsync(Guid ruleId, CancellationToken cancellationToken = default)
    {
        var affected = await _failureHandler.ExecuteAsync<int>("AutomationRules.Delete", async ct =>
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM AutomationRules WHERE RuleId = $id;";
            cmd.Parameters.AddWithValue("$id", ruleId.ToString("D"));
            return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        if (affected.IsFailure) return Result.Failure(affected.Error!);
        return affected.Value > 0
            ? Result.Success()
            : Result.Failure(NexusError.NotFound($"Automação '{ruleId:D}' não encontrada.", "AUTO_RULE_NOT_FOUND"));
    }
}

/// <summary>
/// SQLite repository for Nexus Automate execution history and failure logs.
/// </summary>
public sealed class SqliteAutomationExecutionRepository : IAutomationExecutionRepository
{
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly StorageFailureHandler _failureHandler;

    public SqliteAutomationExecutionRepository(
        ISqliteConnectionFactory connectionFactory,
        StorageFailureHandler failureHandler)
    {
        _connectionFactory = connectionFactory;
        _failureHandler = failureHandler;
    }

    public Task<Result> AppendAsync(AutomationExecutionRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        return _failureHandler.ExecuteAsync("AutomationExecutions.Append", async ct =>
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO AutomationExecutions (
                    ExecutionId, RuleId, RuleName, TriggerType, Outcome, Detail, CorrelationId, DurationMs, ExecutedAtUtc, ContractVersion
                )
                VALUES ($execId, $ruleId, $ruleName, $trigger, $outcome, $detail, $corrId, $duration, $executedAt, $version);
                """;
            cmd.Parameters.AddWithValue("$execId", record.ExecutionId.ToString("D"));
            cmd.Parameters.AddWithValue("$ruleId", record.RuleId.ToString("D"));
            cmd.Parameters.AddWithValue("$ruleName", record.RuleName);
            cmd.Parameters.AddWithValue("$trigger", (int)record.Trigger);
            cmd.Parameters.AddWithValue("$outcome", (int)record.Outcome);
            cmd.Parameters.AddWithValue("$detail", record.Detail);
            cmd.Parameters.AddWithValue("$corrId", record.CorrelationId.ToString("D"));
            cmd.Parameters.AddWithValue("$duration", record.DurationMs);
            cmd.Parameters.AddWithValue("$executedAt", record.ExecutedAtUtc.ToString("O"));
            cmd.Parameters.AddWithValue("$version", record.ContractVersion);

            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, cancellationToken);
    }

    public Task<Result<IReadOnlyList<AutomationExecutionRecord>>> ListRecentAsync(int limit = 100, CancellationToken cancellationToken = default)
    {
        var bounded = Math.Clamp(limit, 1, 500);
        return _failureHandler.ExecuteAsync<IReadOnlyList<AutomationExecutionRecord>>("AutomationExecutions.ListRecent", async ct =>
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT ExecutionId, RuleId, RuleName, TriggerType, Outcome, Detail, CorrelationId, DurationMs, ExecutedAtUtc, ContractVersion
                FROM AutomationExecutions
                ORDER BY ExecutedAtUtc DESC
                LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$limit", bounded);

            var items = new List<AutomationExecutionRecord>();
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                items.Add(new AutomationExecutionRecord
                {
                    ExecutionId = Guid.Parse(reader.GetString(0)),
                    RuleId = Guid.Parse(reader.GetString(1)),
                    RuleName = reader.GetString(2),
                    Trigger = (AutomationTriggerType)reader.GetInt32(3),
                    Outcome = (AutomationExecutionOutcome)reader.GetInt32(4),
                    Detail = reader.GetString(5),
                    CorrelationId = Guid.Parse(reader.GetString(6)),
                    DurationMs = reader.GetInt64(7),
                    ExecutedAtUtc = DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture),
                    ContractVersion = reader.GetString(9)
                });
            }

            return items.AsReadOnly();
        }, cancellationToken);
    }

    public Task<Result<int>> ClearAsync(CancellationToken cancellationToken = default)
    {
        return _failureHandler.ExecuteAsync("AutomationExecutions.Clear", async ct =>
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM AutomationExecutions;";
            return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, cancellationToken);
    }
}

