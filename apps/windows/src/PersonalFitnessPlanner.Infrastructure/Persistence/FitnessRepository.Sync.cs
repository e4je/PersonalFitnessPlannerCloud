using System.Text.Json;
using Microsoft.Data.Sqlite;
using PersonalFitnessPlanner.Contracts;
using PersonalFitnessPlanner.Infrastructure.Models;

namespace PersonalFitnessPlanner.Infrastructure.Persistence;

public sealed partial class FitnessRepository
{
    public async Task<IReadOnlyList<OutboxItem>> GetPendingOutboxAsync(
        int maximum = 100,
        CancellationToken cancellationToken = default)
    {
        if (maximum is < 1 or > 1_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximum));
        }

        var result = new List<OutboxItem>();
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, entity_type, entity_id, operation, idempotency_key, payload_json,
                attempt_count, created_at
            FROM outbox
            WHERE processed_at IS NULL AND status IN ('pending','failed')
                AND (next_attempt_at IS NULL OR next_attempt_at <= $now)
            ORDER BY created_at LIMIT $maximum;
            """;
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$maximum", maximum);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new OutboxItem(
                Guid.Parse(reader.GetString(0)), reader.GetString(1), Guid.Parse(reader.GetString(2)),
                reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetInt32(6),
                DateTimeOffset.Parse(reader.GetString(7))));
        }
        return result;
    }

    public async Task<IReadOnlyList<OutboxItem>> ClaimPendingOutboxAsync(
        int maximum = 100,
        CancellationToken cancellationToken = default)
    {
        if (maximum is < 1 or > 1_000) throw new ArgumentOutOfRangeException(nameof(maximum));
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = DateTimeOffset.UtcNow;
            await using (var recover = connection.CreateCommand())
            {
                recover.Transaction = (SqliteTransaction)transaction;
                recover.CommandText = """
                    UPDATE outbox SET status='pending', in_flight_at=NULL, lock_token=NULL
                    WHERE status='in_flight' AND in_flight_at < $stale;
                    """;
                recover.Parameters.AddWithValue("$stale", now.AddMinutes(-10).ToString("O"));
                await recover.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            var items = new List<OutboxItem>();
            await using (var select = connection.CreateCommand())
            {
                select.Transaction = (SqliteTransaction)transaction;
                select.CommandText = """
                    SELECT id, entity_type, entity_id, operation, idempotency_key, payload_json,
                        attempt_count, created_at
                    FROM outbox WHERE processed_at IS NULL AND status IN ('pending','failed')
                        AND (next_attempt_at IS NULL OR next_attempt_at <= $now)
                    ORDER BY created_at LIMIT $maximum;
                    """;
                select.Parameters.AddWithValue("$now", now.ToString("O"));
                select.Parameters.AddWithValue("$maximum", maximum);
                await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    items.Add(new OutboxItem(Guid.Parse(reader.GetString(0)), reader.GetString(1), Guid.Parse(reader.GetString(2)),
                        reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetInt32(6),
                        DateTimeOffset.Parse(reader.GetString(7))));
                }
            }

            var lockToken = Guid.NewGuid().ToString("D");
            foreach (var item in items)
            {
                await using var claim = connection.CreateCommand();
                claim.Transaction = (SqliteTransaction)transaction;
                claim.CommandText = """
                    UPDATE outbox SET status='in_flight', in_flight_at=$now, lock_token=$lock,
                        updated_at=$now WHERE id=$id AND status IN ('pending','failed');
                    """;
                claim.Parameters.AddWithValue("$now", now.ToString("O"));
                claim.Parameters.AddWithValue("$lock", lockToken);
                claim.Parameters.AddWithValue("$id", item.Id.ToString("D"));
                await claim.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return items;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task MarkOutboxSucceededAsync(
        IEnumerable<Guid> outboxIds,
        CancellationToken cancellationToken = default)
    {
        var ids = outboxIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow.ToString("O");
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var id in ids)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = (SqliteTransaction)transaction;
                command.CommandText = """
                    UPDATE outbox SET processed_at=$now, updated_at=$now, last_error=NULL,
                        next_attempt_at=NULL, status='synced', in_flight_at=NULL, lock_token=NULL
                    WHERE id=$id AND processed_at IS NULL;
                    """;
                command.Parameters.AddWithValue("$now", now);
                command.Parameters.AddWithValue("$id", id.ToString("D"));
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task MarkOutboxFailedAsync(
        IEnumerable<Guid> outboxIds,
        string error,
        CancellationToken cancellationToken = default)
    {
        var ids = outboxIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return;
        }

        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var id in ids)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = (SqliteTransaction)transaction;
                command.CommandText = """
                    UPDATE outbox SET attempt_count=attempt_count+1, last_error=$error,
                        next_attempt_at=$nextAttempt, updated_at=$now, status='failed',
                        in_flight_at=NULL, lock_token=NULL
                    WHERE id=$id AND processed_at IS NULL;
                    """;
                command.Parameters.AddWithValue("$error", error.Length > 2_000 ? error[..2_000] : error);
                command.Parameters.AddWithValue("$nextAttempt", DateTimeOffset.UtcNow.AddSeconds(BackoffSecondsFor(id, ids.Length)).ToString("O"));
                command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
                command.Parameters.AddWithValue("$id", id.ToString("D"));
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }

    }

    public async Task RecordSyncBatchFailuresAsync(
        IReadOnlyList<SyncBatchFailure> failures,
        CancellationToken cancellationToken = default)
    {
        if (failures.Count == 0) return;
        var now = DateTimeOffset.UtcNow;
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var failure in failures.GroupBy(item => item.OutboxId).Select(group => group.Last()))
            {
                var detail = string.IsNullOrWhiteSpace(failure.Error)
                    ? $"服务器返回 {failure.Status}，未提供错误详情。"
                    : $"{failure.Status}: {failure.Error}";
                if (failure.ServerVersion is not null) detail += $" (server_version={failure.ServerVersion})";
                if (detail.Length > 2_000) detail = detail[..2_000];
                var serverJson = string.IsNullOrWhiteSpace(failure.ServerCopyJson)
                    ? ContractJson.Serialize(new
                    {
                        status = failure.Status,
                        error = failure.Error,
                        server_version = failure.ServerVersion
                    })
                    : failure.ServerCopyJson;

                await using (var conflict = connection.CreateCommand())
                {
                    conflict.Transaction = (SqliteTransaction)transaction;
                    conflict.CommandText = """
                        INSERT INTO sync_conflicts(id, entity_type, entity_id, local_json, server_json,
                            resolution, created_at, resolved_at)
                        SELECT $conflictId, o.entity_type, o.entity_id, o.payload_json, $serverJson,
                            'outbox_failed', $now, NULL
                        FROM outbox o
                        WHERE o.id=$outboxId AND o.processed_at IS NULL
                          AND NOT EXISTS(
                              SELECT 1 FROM sync_conflicts c
                              WHERE c.entity_type=o.entity_type AND c.entity_id=o.entity_id
                                AND c.server_json=$serverJson AND c.resolved_at IS NULL)
                        LIMIT 1;
                        """;
                    conflict.Parameters.AddWithValue("$conflictId", Guid.NewGuid().ToString("D"));
                    conflict.Parameters.AddWithValue("$outboxId", failure.OutboxId.ToString("D"));
                    conflict.Parameters.AddWithValue("$serverJson", serverJson);
                    conflict.Parameters.AddWithValue("$now", now.ToString("O"));
                    await conflict.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await using var update = connection.CreateCommand();
                update.Transaction = (SqliteTransaction)transaction;
                update.CommandText = """
                    UPDATE outbox SET attempt_count=attempt_count+1, last_error=$error,
                        next_attempt_at=$nextAttempt, updated_at=$now, status='failed',
                        in_flight_at=NULL, lock_token=NULL
                    WHERE id=$id AND processed_at IS NULL;
                    """;
                update.Parameters.AddWithValue("$error", detail);
                update.Parameters.AddWithValue("$nextAttempt", now.AddSeconds(
                    BackoffSecondsFor(failure.OutboxId, failures.Count)).ToString("O"));
                update.Parameters.AddWithValue("$now", now.ToString("O"));
                update.Parameters.AddWithValue("$id", failure.OutboxId.ToString("D"));
                await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static int BackoffSecondsFor(Guid id, int batchSize)
    {
        // A small deterministic jitter prevents clients retrying in lockstep.
        return Math.Min(3_600, 30 * Math.Max(1, batchSize)) + Math.Abs(id.GetHashCode() % 15);
    }

    public async Task<string> GetSyncCursorAsync(string stream = "changes", CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT cursor FROM sync_state WHERE stream=$stream;";
        command.Parameters.AddWithValue("$stream", stream);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string ?? string.Empty;
    }

    public async Task SetSyncCursorAsync(
        string cursor,
        string stream = "changes",
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sync_state(stream, cursor, last_synced_at) VALUES ($stream, $cursor, $now)
            ON CONFLICT(stream) DO UPDATE SET cursor=excluded.cursor, last_synced_at=excluded.last_synced_at;
            """;
        command.Parameters.AddWithValue("$stream", stream);
        command.Parameters.AddWithValue("$cursor", cursor ?? string.Empty);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<OutboxStatusData> GetOutboxStatusAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        int pending;
        int failed;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT COUNT(*), COALESCE(SUM(CASE WHEN status='failed' THEN 1 ELSE 0 END),0)
                FROM outbox WHERE processed_at IS NULL;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            pending = reader.GetInt32(0);
            failed = reader.GetInt32(1);
        }

        DateTimeOffset? lastSync = null;
        string cursor = string.Empty;
        string lastError = string.Empty;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT cursor, last_synced_at FROM sync_state WHERE stream='changes';";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                cursor = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                lastSync = reader.IsDBNull(1) ? null : DateTimeOffset.Parse(reader.GetString(1));
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT COALESCE(last_error, '') FROM outbox
                WHERE processed_at IS NULL AND status='failed'
                ORDER BY updated_at DESC LIMIT 1;
                """;
            lastError = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string ?? string.Empty;
        }

        return new OutboxStatusData(pending, failed, lastSync, cursor, lastError);
    }

    /// <summary>Applies server-authoritative plan/session changes in one local transaction.</summary>
    public async Task ApplyServerChangesAsync(
        IReadOnlyList<SyncChange> changes,
        CancellationToken cancellationToken = default)
    {
        if (changes.Count == 0)
        {
            return;
        }

        var catalog = await LoadCloudCatalogAsync(cancellationToken).ConfigureAwait(false);
        var exerciseModels = catalog.Exercises.ToDictionary(x => x.Key, x => x.Value);
        var equipmentNames = catalog.Equipment.ToDictionary(x => x.Key, x => x.Value);
        PlanData? currentPlan = null;
        try { currentPlan = await GetCurrentPlanAsync(cancellationToken).ConfigureAwait(false); }
        catch (InvalidOperationException) { }
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var change in changes.OrderBy(change => EntityApplyOrder(NormalizeEntityType(change.EntityType))))
            {
                await RecordConflictIfNeededAsync(connection, (SqliteTransaction)transaction, change, cancellationToken).ConfigureAwait(false);
                var entityType = NormalizeEntityType(change.EntityType);
                if (string.Equals(change.Operation, "delete", StringComparison.OrdinalIgnoreCase))
                {
                    await ApplyServerDeleteAsync(connection, (SqliteTransaction)transaction, entityType, change, cancellationToken)
                        .ConfigureAwait(false);
                    if (entityType == "equipment") equipmentNames.Remove(change.EntityId);
                    if (entityType == "exercise") exerciseModels.Remove(change.EntityId);
                    if (entityType is "plan" or "plan_version" && currentPlan?.Id == change.EntityId) currentPlan = null;
                    continue;
                }

                if (entityType is "plan" or "plan_version")
                {
                    PlanVersionDto? dto = null;
                    try { dto = ContractJson.Deserialize<PlanVersionDto>(change.PayloadJson); }
                    catch (JsonException) { }
                    PlanData plan;
                    if (dto is not null && dto.Id != Guid.Empty && dto.PlanId != Guid.Empty)
                    {
                        plan = ConvertPlan(dto, exerciseModels, equipmentNames);
                    }
                    else
                    {
                        plan = DeserializePlan(change.PayloadJson);
                    }
                    await UpsertServerPlanAsync(connection, (SqliteTransaction)transaction, plan, cancellationToken,
                        dto?.DeletedAt, dto?.Version ?? change.Version).ConfigureAwait(false);
                    currentPlan = plan;
                }
                else if (entityType == "user")
                {
                    var user = ContractJson.Deserialize<UserDto>(change.PayloadJson)
                        ?? throw new InvalidDataException("同步用户为空。");
                    await UpsertUserAsync(connection, (SqliteTransaction)transaction, user, cancellationToken)
                        .ConfigureAwait(false);
                }
                else if (entityType is "equipment")
                {
                    var equipment = ContractJson.Deserialize<EquipmentDto>(change.PayloadJson)
                        ?? throw new InvalidDataException("同步器械为空。");
                    await UpsertEquipmentAsync(connection, (SqliteTransaction)transaction, equipment, cancellationToken).ConfigureAwait(false);
                    if (equipment.DeletedAt is null) equipmentNames[equipment.Id] = equipment.Name;
                    else equipmentNames.Remove(equipment.Id);
                }
                else if (entityType is "exercise")
                {
                    var exercise = ContractJson.Deserialize<ExerciseDto>(change.PayloadJson)
                        ?? throw new InvalidDataException("同步动作为空。");
                    await UpsertExerciseAsync(connection, (SqliteTransaction)transaction, exercise, equipmentNames,
                        [exercise], cancellationToken).ConfigureAwait(false);
                    var model = ToExerciseLibraryItem(exercise, equipmentNames, [exercise]);
                    if (exercise.DeletedAt is null) exerciseModels[exercise.Id] = model;
                    else exerciseModels.Remove(exercise.Id);
                }
                else if (entityType is "plan_assignment" or "assignment")
                {
                    var assignment = ContractJson.Deserialize<PlanAssignmentDto>(change.PayloadJson)
                        ?? throw new InvalidDataException("同步计划分配为空。");
                    if (assignment.IsActive && assignment.DeletedAt is null)
                    {
                        await using var deactivate = connection.CreateCommand();
                        deactivate.Transaction = (SqliteTransaction)transaction;
                        deactivate.CommandText = "UPDATE plan_assignments SET is_active=0 WHERE is_active=1;";
                        await deactivate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    }
                    await UpsertAssignmentAsync(connection, (SqliteTransaction)transaction, assignment, cancellationToken).ConfigureAwait(false);
                }
                else if (entityType == "workout_session")
                {
                    WorkoutSessionDto? dto = null;
                    try { dto = ContractJson.Deserialize<WorkoutSessionDto>(change.PayloadJson); }
                    catch (JsonException) { }
                    WorkoutExportSession session;
                    if (dto is not null && dto.Id != Guid.Empty && dto.StartedAt != default)
                    {
                        session = ConvertWorkout(dto, currentPlan, exerciseModels, equipmentNames);
                    }
                    else
                    {
                        session = Deserialize<WorkoutExportSession>(change.PayloadJson);
                    }
                    await UpsertServerSessionAsync(connection, (SqliteTransaction)transaction, session, cancellationToken).ConfigureAwait(false);
                }
                else if (entityType == "workout_set")
                {
                    var dto = ContractJson.Deserialize<WorkoutSetDto>(change.PayloadJson)
                        ?? throw new InvalidDataException("同步训练组为空。");
                    var set = new SavedSetData(
                        dto.Id,
                        dto.SessionId,
                        dto.PlanSlotId ?? Guid.Empty,
                        dto.SourcePlanSlotOptionId ?? Guid.Empty,
                        dto.SetNumber,
                        dto.WeightKg is null ? null : Convert.ToDecimal(dto.WeightKg.Value),
                        dto.Reps,
                        dto.DurationSeconds,
                        dto.Rir,
                        dto.Pain,
                        dto.Notes ?? string.Empty,
                        dto.CompletedAt ?? dto.UpdatedAt ?? change.UpdatedAt,
                        dto.ExerciseId,
                        dto.EquipmentId is { } equipmentId
                            ? equipmentNames.GetValueOrDefault(equipmentId, string.Empty)
                            : string.Empty,
                        dto.IsWarmup,
                        dto.EquipmentId,
                        dto.Version,
                        dto.DeletedAt);
                    var exerciseName = exerciseModels.GetValueOrDefault(dto.ExerciseId)?.Name ?? dto.ExerciseId.ToString("D");
                    await UpsertServerSetAsync(connection, (SqliteTransaction)transaction, set, exerciseName, cancellationToken)
                        .ConfigureAwait(false);
                }
                else if (entityType == "daily_readiness")
                {
                    var readiness = ContractJson.Deserialize<ReadinessDto>(change.PayloadJson)
                        ?? throw new InvalidDataException("同步每日状态为空。");
                    await UpsertServerReadinessAsync(connection, (SqliteTransaction)transaction, readiness, cancellationToken)
                        .ConfigureAwait(false);
                }
                else if (entityType == "cardio_session")
                {
                    var cardio = ContractJson.Deserialize<CardioSessionDto>(change.PayloadJson)
                        ?? throw new InvalidDataException("同步有氧记录为空。");
                    await UpsertServerCardioAsync(connection, (SqliteTransaction)transaction, cardio, cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    throw new InvalidDataException($"不支持的同步实体类型：{change.EntityType}");
                }
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static string NormalizeEntityType(string entityType) =>
        entityType.Trim().Replace('-', '_').ToLowerInvariant() switch
        {
            "readiness" => "daily_readiness",
            "cardio" => "cardio_session",
            "assignment" => "plan_assignment",
            _ => entityType.Trim().Replace('-', '_').ToLowerInvariant()
        };

    private static int EntityApplyOrder(string entityType) => entityType switch
    {
        "user" => 0,
        "equipment" => 1,
        "exercise" => 2,
        "plan" or "plan_version" => 3,
        "plan_assignment" => 4,
        "workout_session" => 5,
        "workout_set" => 6,
        "daily_readiness" => 7,
        "cardio_session" => 8,
        _ => 100
    };

    private static async Task ApplyServerDeleteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string entityType,
        SyncChange change,
        CancellationToken cancellationToken)
    {
        var deletedAt = ReadDeletedAt(change.PayloadJson) ?? change.UpdatedAt;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = entityType switch
        {
            "user" => "UPDATE user_cache SET deleted_at=$deletedAt, updated_at=$deletedAt, entity_version=MAX(entity_version,$version) WHERE id=$id;",
            "plan" or "plan_version" => """
                UPDATE plans SET deleted_at=$deletedAt, updated_at=$deletedAt,
                    entity_version=MAX(entity_version,$version) WHERE id=$id;
                UPDATE plan_assignments SET is_active=0 WHERE plan_version_id=$id;
                """,
            "equipment" => "UPDATE equipment_cache SET deleted_at=$deletedAt, updated_at=$deletedAt, entity_version=MAX(entity_version,$version) WHERE id=$id;",
            "exercise" => "UPDATE exercises SET deleted_at=$deletedAt, updated_at=$deletedAt WHERE id=$id;",
            "plan_assignment" => "UPDATE plan_assignments SET is_active=0 WHERE id=$id;",
            "workout_session" => """
                UPDATE workout_sessions SET status='completed', deleted_at=$deletedAt, updated_at=$deletedAt,
                    entity_version=MAX(entity_version,$version) WHERE id=$id;
                UPDATE workout_sets SET deleted_at=COALESCE(deleted_at,$deletedAt), updated_at=$deletedAt
                    WHERE session_id=$id;
                """,
            "workout_set" => "UPDATE workout_sets SET deleted_at=$deletedAt, updated_at=$deletedAt, entity_version=MAX(entity_version,$version) WHERE id=$id;",
            "daily_readiness" => "UPDATE daily_readiness SET deleted_at=$deletedAt, updated_at=$deletedAt, entity_version=MAX(entity_version,$version) WHERE id=$id;",
            "cardio_session" => "UPDATE cardio_sessions SET deleted_at=$deletedAt, updated_at=$deletedAt, entity_version=MAX(entity_version,$version) WHERE id=$id;",
            _ => throw new InvalidDataException($"不支持删除同步实体：{entityType}")
        };
        command.Parameters.AddWithValue("$id", change.EntityId.ToString("D"));
        command.Parameters.AddWithValue("$deletedAt", deletedAt.ToString("O"));
        command.Parameters.AddWithValue("$version", change.Version);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static DateTimeOffset? ReadDeletedAt(string payloadJson)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                (document.RootElement.TryGetProperty("deleted_at", out var value) ||
                 document.RootElement.TryGetProperty("deletedAt", out value)) &&
                value.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(value.GetString(), out var parsed))
            {
                return parsed;
            }
        }
        catch (JsonException) { }
        return null;
    }

    public async Task<IReadOnlyList<WorkoutExportSession>> GetWorkoutExportSessionsAsync(
        CancellationToken cancellationToken = default)
    {
        var sessions = new List<WorkoutExportSession>();
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, day_code, local_date, status, source, plan_version_id, plan_snapshot_json,
                started_at, completed_at, ended_early, deleted_at, plan_assignment_id, plan_day_id,
                timezone, entity_version
            FROM workout_sessions ORDER BY started_at;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var raw = new List<(Guid Id, string DayCode, DateOnly LocalDate, string Status, string Source,
            Guid PlanVersionId, PlanData Snapshot, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt,
            bool EndedEarly, DateTimeOffset? DeletedAt, Guid? PlanAssignmentId, Guid? PlanDayId,
            string Timezone, long ServerVersion)>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            raw.Add((
                Guid.Parse(reader.GetString(0)), reader.GetString(1), DateOnly.ParseExact(reader.GetString(2), "yyyy-MM-dd"),
                reader.GetString(3), reader.GetString(4), Guid.Parse(reader.GetString(5)), DeserializePlan(reader.GetString(6)),
                DateTimeOffset.Parse(reader.GetString(7)), reader.IsDBNull(8) ? null : DateTimeOffset.Parse(reader.GetString(8)),
                reader.GetInt32(9) != 0, reader.IsDBNull(10) ? null : DateTimeOffset.Parse(reader.GetString(10)),
                reader.IsDBNull(11) ? null : Guid.Parse(reader.GetString(11)),
                reader.IsDBNull(12) ? null : Guid.Parse(reader.GetString(12)), reader.GetString(13), reader.GetInt64(14)));
        }
        await reader.DisposeAsync().ConfigureAwait(false);

        foreach (var entry in raw)
        {
            var sets = await GetSetsIncludingDeletedAsync(connection, entry.Id, cancellationToken).ConfigureAwait(false);
            sessions.Add(new WorkoutExportSession(entry.Id, entry.DayCode, entry.LocalDate, entry.Status,
                entry.Source, entry.PlanVersionId, entry.Snapshot, entry.StartedAt, entry.CompletedAt,
                entry.EndedEarly, entry.DeletedAt, sets, entry.PlanAssignmentId, entry.PlanDayId,
                entry.Timezone, entry.ServerVersion));
        }
        return sessions;
    }

    public async Task ImportSnapshotAsync(HistoryExport export, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(export);
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var plan in export.Plans)
            {
                ValidatePlan(plan);
                await UpsertServerPlanAsync(connection, (SqliteTransaction)transaction, plan, cancellationToken).ConfigureAwait(false);
            }
            foreach (var session in export.WorkoutSessions)
            {
                await UpsertServerSessionAsync(connection, (SqliteTransaction)transaction, session, cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<IReadOnlyList<SavedSetData>> GetSetsIncludingDeletedAsync(
        SqliteConnection connection,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        var result = new List<SavedSetData>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, session_id, plan_item_id, option_id, set_number, weight_kg, reps,
                duration_seconds, rir, pain, notes, completed_at, exercise_id, equipment_key, is_warmup,
                equipment_id, entity_version, deleted_at
            FROM workout_sets WHERE session_id=$sessionId ORDER BY completed_at, set_number;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(ReadSavedSet(reader));
        }
        return result;
    }

    private static async Task RecordConflictIfNeededAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SyncChange change,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO sync_conflicts(id, entity_type, entity_id, local_json, server_json,
                resolution, created_at, resolved_at)
            SELECT $id, $entityType, $entityId, o.payload_json, $serverJson,
                'server_wins', $now, $now
            FROM outbox o
            WHERE o.entity_id=$entityId AND o.processed_at IS NULL
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$entityType", change.EntityType);
        command.Parameters.AddWithValue("$entityId", change.EntityId.ToString("D"));
        command.Parameters.AddWithValue("$serverJson", change.PayloadJson);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpsertServerPlanAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PlanData plan,
        CancellationToken cancellationToken,
        DateTimeOffset? deletedAt = null,
        long entityVersion = 0)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO plans(id, plan_id, name, version, status, intro_weeks, intro_max_sets,
                snapshot_json, published_at, created_at, updated_at, deleted_at, entity_version)
            VALUES ($id, $planId, $name, $version, $status, $introWeeks, $introMaxSets,
                $snapshot, $publishedAt, $now, $now, $deletedAt, $entityVersion)
            ON CONFLICT(id) DO UPDATE SET plan_id=excluded.plan_id, name=excluded.name,
                version=excluded.version, status=excluded.status, intro_weeks=excluded.intro_weeks,
                intro_max_sets=excluded.intro_max_sets, snapshot_json=excluded.snapshot_json,
                published_at=excluded.published_at, updated_at=excluded.updated_at,
                deleted_at=excluded.deleted_at, entity_version=excluded.entity_version
            WHERE excluded.entity_version=0 OR excluded.entity_version >= plans.entity_version;
            """;
        command.Parameters.AddWithValue("$id", plan.Id.ToString("D"));
        command.Parameters.AddWithValue("$planId", plan.PlanId.ToString("D"));
        command.Parameters.AddWithValue("$name", plan.Name);
        command.Parameters.AddWithValue("$version", plan.Version);
        command.Parameters.AddWithValue("$status", plan.Status.ToLowerInvariant());
        command.Parameters.AddWithValue("$introWeeks", plan.DeloadWeeks);
        command.Parameters.AddWithValue("$introMaxSets", plan.DeloadMaxSets);
        command.Parameters.AddWithValue("$snapshot", Serialize(plan));
        command.Parameters.AddWithValue("$publishedAt", plan.PublishedAt is null ? DBNull.Value : plan.PublishedAt.Value.ToString("O"));
        command.Parameters.AddWithValue("$deletedAt", deletedAt is null ? DBNull.Value : deletedAt.Value.ToString("O"));
        command.Parameters.AddWithValue("$entityVersion", entityVersion);
        command.Parameters.AddWithValue("$now", now);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpsertServerSessionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        WorkoutExportSession session,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO workout_sessions(id, day_code, local_date, status, source, plan_version_id,
                    plan_snapshot_json, started_at, completed_at, ended_early, created_at, updated_at, deleted_at,
                    effective_set_cap, plan_assignment_id, plan_day_id, timezone, entity_version)
                VALUES ($id, $dayCode, $localDate, $status, $source, $planVersionId, $snapshot,
                    $startedAt, $completedAt, $endedEarly, $startedAt, $now, $deletedAt, NULL,
                    $planAssignmentId, $planDayId, $timezone, $entityVersion)
                ON CONFLICT(id) DO UPDATE SET day_code=excluded.day_code, local_date=excluded.local_date,
                    status=excluded.status, source=excluded.source, plan_version_id=excluded.plan_version_id,
                    plan_snapshot_json=excluded.plan_snapshot_json, started_at=excluded.started_at,
                    completed_at=excluded.completed_at,
                    ended_early=excluded.ended_early, updated_at=excluded.updated_at,
                    deleted_at=excluded.deleted_at, plan_assignment_id=excluded.plan_assignment_id,
                    plan_day_id=excluded.plan_day_id, timezone=excluded.timezone,
                    entity_version=excluded.entity_version
                WHERE excluded.entity_version=0 OR excluded.entity_version >= workout_sessions.entity_version;
                """;
            command.Parameters.AddWithValue("$id", session.Id.ToString("D"));
            command.Parameters.AddWithValue("$dayCode", session.DayCode);
            command.Parameters.AddWithValue("$localDate", session.LocalDate.ToString("yyyy-MM-dd"));
            command.Parameters.AddWithValue("$status", session.Status);
            command.Parameters.AddWithValue("$source", session.Source);
            command.Parameters.AddWithValue("$planVersionId", session.PlanVersionId.ToString("D"));
            command.Parameters.AddWithValue("$snapshot", Serialize(session.PlanSnapshot));
            command.Parameters.AddWithValue("$startedAt", session.StartedAt.ToString("O"));
            command.Parameters.AddWithValue("$completedAt", session.CompletedAt is null ? DBNull.Value : session.CompletedAt.Value.ToString("O"));
            command.Parameters.AddWithValue("$endedEarly", session.EndedEarly ? 1 : 0);
            command.Parameters.AddWithValue("$now", now);
            command.Parameters.AddWithValue("$deletedAt", session.DeletedAt is null ? DBNull.Value : session.DeletedAt.Value.ToString("O"));
            command.Parameters.AddWithValue("$planAssignmentId", session.PlanAssignmentId is null
                ? DBNull.Value
                : session.PlanAssignmentId.Value.ToString("D"));
            command.Parameters.AddWithValue("$planDayId", session.PlanDayId is null
                ? DBNull.Value
                : session.PlanDayId.Value.ToString("D"));
            command.Parameters.AddWithValue("$timezone", string.IsNullOrWhiteSpace(session.Timezone) ? "UTC" : session.Timezone);
            command.Parameters.AddWithValue("$entityVersion", session.ServerVersion);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var set in session.Sets)
        {
            var exerciseName = session.PlanSnapshot.Days.SelectMany(x => x.Items).SelectMany(x => x.Options)
                .FirstOrDefault(x => x.Id == set.OptionId)?.ExerciseName ?? set.ExerciseId.ToString("D");
            await UpsertServerSetAsync(connection, transaction, set, exerciseName, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task UpsertServerSetAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SavedSetData set,
        string exerciseName,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO workout_sets(id, session_id, plan_item_id, option_id, exercise_name,
                set_number, weight_kg, reps, duration_seconds, rir, pain, notes, client_set_key,
                completed_at, created_at, updated_at, deleted_at, exercise_id, equipment_key,
                is_warmup, equipment_id, entity_version)
            VALUES ($id, $sessionId, $planItemId, $optionId, $exerciseName, $setNumber, $weightKg,
                $reps, $durationSeconds, $rir, $pain, $notes, $clientSetKey,
                $completedAt, $completedAt, $now, $deletedAt, $exerciseId, $equipmentKey,
                $isWarmup, $equipmentId, $entityVersion)
            ON CONFLICT(id) DO UPDATE SET session_id=excluded.session_id,
                plan_item_id=excluded.plan_item_id, option_id=excluded.option_id,
                exercise_name=excluded.exercise_name, set_number=excluded.set_number,
                weight_kg=excluded.weight_kg, reps=excluded.reps,
                duration_seconds=excluded.duration_seconds, rir=excluded.rir, pain=excluded.pain,
                notes=excluded.notes, completed_at=excluded.completed_at,
                exercise_id=excluded.exercise_id, equipment_key=excluded.equipment_key,
                is_warmup=excluded.is_warmup, equipment_id=excluded.equipment_id,
                entity_version=excluded.entity_version, deleted_at=excluded.deleted_at,
                updated_at=excluded.updated_at
            WHERE excluded.entity_version=0 OR excluded.entity_version >= workout_sets.entity_version;
            """;
        command.Parameters.AddWithValue("$id", set.Id.ToString("D"));
        command.Parameters.AddWithValue("$sessionId", set.SessionId.ToString("D"));
        command.Parameters.AddWithValue("$planItemId", set.PlanItemId.ToString("D"));
        command.Parameters.AddWithValue("$optionId", set.OptionId.ToString("D"));
        command.Parameters.AddWithValue("$exerciseName", exerciseName);
        command.Parameters.AddWithValue("$setNumber", set.SetNumber);
        command.Parameters.AddWithValue("$weightKg", set.WeightKg is null ? DBNull.Value : set.WeightKg.Value);
        command.Parameters.AddWithValue("$reps", set.Reps is null ? DBNull.Value : set.Reps.Value);
        command.Parameters.AddWithValue("$durationSeconds", set.DurationSeconds is null ? DBNull.Value : set.DurationSeconds.Value);
        command.Parameters.AddWithValue("$rir", set.Rir is null ? DBNull.Value : set.Rir.Value);
        command.Parameters.AddWithValue("$pain", set.Pain ? 1 : 0);
        command.Parameters.AddWithValue("$notes", set.Notes);
        command.Parameters.AddWithValue("$clientSetKey", $"server:{set.Id:D}");
        command.Parameters.AddWithValue("$completedAt", set.CompletedAt.ToString("O"));
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$deletedAt", set.DeletedAt is null ? DBNull.Value : set.DeletedAt.Value.ToString("O"));
        command.Parameters.AddWithValue("$exerciseId", set.ExerciseId == Guid.Empty ? DBNull.Value : set.ExerciseId.ToString("D"));
        command.Parameters.AddWithValue("$equipmentKey", set.Equipment ?? string.Empty);
        command.Parameters.AddWithValue("$isWarmup", set.IsWarmup ? 1 : 0);
        command.Parameters.AddWithValue("$equipmentId", set.EquipmentId is null
            ? DBNull.Value
            : set.EquipmentId.Value.ToString("D"));
        command.Parameters.AddWithValue("$entityVersion", set.ServerVersion);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
