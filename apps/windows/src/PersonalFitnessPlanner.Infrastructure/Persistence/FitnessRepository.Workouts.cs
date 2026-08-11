using Microsoft.Data.Sqlite;
using PersonalFitnessPlanner.Infrastructure.Models;

namespace PersonalFitnessPlanner.Infrastructure.Persistence;

public sealed partial class FitnessRepository
{
    public async Task<ActiveWorkoutData> StartWorkoutAsync(
        string dayCode,
        DateOnly? localDate = null,
        CancellationToken cancellationToken = default,
        string timeZone = "UTC")
    {
        var active = await GetActiveWorkoutAsync(cancellationToken).ConfigureAwait(false);
        if (active is not null)
        {
            return active;
        }

        var normalizedDayCode = dayCode.Trim().ToUpperInvariant();
        var plan = await GetCurrentPlanAsync(cancellationToken).ConfigureAwait(false);
        if (!plan.Days.Any(x => string.Equals(x.Code, normalizedDayCode, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException($"计划中不存在训练日 {normalizedDayCode}。", nameof(dayCode));
        }

        var sessionId = Guid.NewGuid();
        var startedAt = DateTimeOffset.UtcNow;
        var workoutDate = localDate ?? DateOnly.FromDateTime(DateTime.Today);
        var effectiveSetCap = await GetEffectiveSetCapAsync(plan, workoutDate, cancellationToken).ConfigureAwait(false);
        var effectivePlan = ApplySetCap(plan, effectiveSetCap);
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO workout_sessions(
                    id, day_code, local_date, status, source, plan_version_id, plan_snapshot_json,
                    started_at, completed_at, ended_early, created_at, updated_at, deleted_at,
                    effective_set_cap, timezone)
                VALUES ($id, $dayCode, $localDate, 'active', 'windows', $planVersionId, $snapshot,
                    $startedAt, NULL, 0, $startedAt, $startedAt, NULL, $effectiveSetCap, $timezone);
                """;
            command.Parameters.AddWithValue("$id", sessionId.ToString("D"));
            command.Parameters.AddWithValue("$dayCode", normalizedDayCode);
            command.Parameters.AddWithValue("$localDate", workoutDate.ToString("yyyy-MM-dd"));
            command.Parameters.AddWithValue("$planVersionId", plan.Id.ToString("D"));
            command.Parameters.AddWithValue("$snapshot", Serialize(plan));
            command.Parameters.AddWithValue("$startedAt", startedAt.ToString("O"));
            command.Parameters.AddWithValue("$effectiveSetCap", effectiveSetCap is null ? DBNull.Value : effectiveSetCap.Value);
            command.Parameters.AddWithValue("$timezone", string.IsNullOrWhiteSpace(timeZone) ? "UTC" : timeZone.Trim());
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            var payload = new
            {
                id = sessionId,
                client_id = sessionId,
                source = "windows",
                source_device = "windows",
                plan_version_id = plan.Id,
                plan_day_code = normalizedDayCode,
                local_date = workoutDate,
                timezone = string.IsNullOrWhiteSpace(timeZone) ? "UTC" : timeZone.Trim(),
                started_at = startedAt,
                status = "IN_PROGRESS",
                is_full_body = true,
                plan_snapshot_json = Serialize(effectivePlan),
                metadata = new { effective_set_cap = effectiveSetCap }
            };
            await EnqueueOutboxAsync(connection, (SqliteTransaction)transaction, "workout_session", sessionId,
                "upsert", $"session:{sessionId:D}:start", payload, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        return new ActiveWorkoutData(sessionId, normalizedDayCode, workoutDate, effectivePlan, [], startedAt);
    }

    public async Task<ActiveWorkoutData?> GetActiveWorkoutAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, day_code, local_date, plan_snapshot_json, started_at, effective_set_cap
            FROM workout_sessions
            WHERE status='active' AND deleted_at IS NULL
            ORDER BY started_at DESC LIMIT 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var sessionId = Guid.Parse(reader.GetString(0));
        var dayCode = reader.GetString(1);
        var localDate = DateOnly.ParseExact(reader.GetString(2), "yyyy-MM-dd");
        var snapshot = DeserializePlan(reader.GetString(3));
        var startedAt = DateTimeOffset.Parse(reader.GetString(4));
        var effectiveSetCap = reader.IsDBNull(5) ? (int?)null : reader.GetInt32(5);
        await reader.DisposeAsync().ConfigureAwait(false);
        var sets = await GetSetsAsync(connection, sessionId, cancellationToken).ConfigureAwait(false);
        return new ActiveWorkoutData(sessionId, dayCode, localDate, ApplySetCap(snapshot, effectiveSetCap), sets, startedAt);
    }

    /// <summary>
    /// Saves a set once. Returns false when ClientSetKey was already accepted.
    /// The set and its outbox entry commit atomically.
    /// </summary>
    public async Task<bool> SaveSetAsync(SaveSetInput input, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input.ClientSetKey);
        if (input.SetNumber <= 0 || input.WeightKg < 0 || input.Reps < 0 || input.DurationSeconds < 0 || input.Rir is < 0 or > 10)
        {
            throw new ArgumentOutOfRangeException(nameof(input), "组号、重量、次数、时长或 RIR 无效。");
        }

        var setId = Guid.NewGuid();
        var completedAt = DateTimeOffset.UtcNow;
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using (var session = connection.CreateCommand())
            {
                session.Transaction = (SqliteTransaction)transaction;
                session.CommandText = "SELECT COUNT(*) FROM workout_sessions WHERE id=$id AND status='active' AND deleted_at IS NULL;";
                session.Parameters.AddWithValue("$id", input.SessionId.ToString("D"));
                if (Convert.ToInt32(await session.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) != 1)
                {
                    throw new InvalidOperationException("训练会话不存在或已经结束。");
                }
            }

            int inserted;
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = (SqliteTransaction)transaction;
                command.CommandText = """
                    INSERT OR IGNORE INTO workout_sets(
                        id, session_id, plan_item_id, option_id, exercise_name, set_number,
                        weight_kg, reps, duration_seconds, rir, pain, notes, client_set_key,
                        completed_at, created_at, updated_at, deleted_at, exercise_id, equipment_key,
                        is_warmup, equipment_id)
                    VALUES ($id, $sessionId, $planItemId, $optionId, $exerciseName, $setNumber,
                        $weightKg, $reps, $durationSeconds, $rir, $pain, $notes, $clientSetKey,
                        $completedAt, $completedAt, $completedAt, NULL, $exerciseId, $equipmentKey, 0,
                        $equipmentId);
                    """;
                command.Parameters.AddWithValue("$id", setId.ToString("D"));
                command.Parameters.AddWithValue("$sessionId", input.SessionId.ToString("D"));
                command.Parameters.AddWithValue("$planItemId", input.PlanItemId.ToString("D"));
                command.Parameters.AddWithValue("$optionId", input.Option.Id.ToString("D"));
                command.Parameters.AddWithValue("$exerciseName", input.Option.ExerciseName);
                command.Parameters.AddWithValue("$setNumber", input.SetNumber);
                command.Parameters.AddWithValue("$weightKg", input.WeightKg is null ? DBNull.Value : input.WeightKg.Value);
                command.Parameters.AddWithValue("$reps", input.Reps is null ? DBNull.Value : input.Reps.Value);
                command.Parameters.AddWithValue("$durationSeconds", input.DurationSeconds is null ? DBNull.Value : input.DurationSeconds.Value);
                command.Parameters.AddWithValue("$rir", input.Rir is null ? DBNull.Value : input.Rir.Value);
                command.Parameters.AddWithValue("$pain", input.Pain ? 1 : 0);
                command.Parameters.AddWithValue("$notes", input.Notes ?? string.Empty);
                command.Parameters.AddWithValue("$clientSetKey", input.ClientSetKey.Trim());
                command.Parameters.AddWithValue("$completedAt", completedAt.ToString("O"));
                command.Parameters.AddWithValue("$exerciseId", input.Option.ExerciseId.ToString("D"));
                command.Parameters.AddWithValue("$equipmentKey", input.Option.Equipment);
                command.Parameters.AddWithValue("$equipmentId", input.Option.EquipmentId is null
                    ? DBNull.Value
                    : input.Option.EquipmentId.Value.ToString("D"));
                inserted = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            if (inserted == 0)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }

            var saved = new SavedSetData(setId, input.SessionId, input.PlanItemId, input.Option.Id,
                input.SetNumber, input.WeightKg, input.Reps, input.DurationSeconds, input.Rir,
                input.Pain, input.Notes ?? string.Empty, completedAt, input.Option.ExerciseId,
                input.Option.Equipment, false, input.Option.EquipmentId);
            await EnqueueOutboxAsync(connection, (SqliteTransaction)transaction, "workout_set", setId,
                "upsert", input.ClientSetKey.Trim(), CreateWorkoutSetOutboxPayload(saved),
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<SavedSetData?> UpdatePreviousSetAsync(
        Guid sessionId,
        Guid planItemId,
        decimal? weightKg,
        int? reps,
        int? rir,
        bool pain,
        CancellationToken cancellationToken = default)
    {
        if (weightKg < 0 || reps < 0 || rir is < 0 or > 10)
        {
            throw new ArgumentOutOfRangeException(nameof(weightKg), "重量、次数或 RIR 无效。");
        }

        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Guid? setId = null;
            await using (var find = connection.CreateCommand())
            {
                find.Transaction = (SqliteTransaction)transaction;
                find.CommandText = """
                    SELECT id FROM workout_sets
                    WHERE session_id=$sessionId AND plan_item_id=$planItemId AND deleted_at IS NULL
                    ORDER BY completed_at DESC LIMIT 1;
                    """;
                find.Parameters.AddWithValue("$sessionId", sessionId.ToString("D"));
                find.Parameters.AddWithValue("$planItemId", planItemId.ToString("D"));
                if (await find.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string value)
                {
                    setId = Guid.Parse(value);
                }
            }

            if (setId is null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }

            var now = DateTimeOffset.UtcNow;
            await using (var update = connection.CreateCommand())
            {
                update.Transaction = (SqliteTransaction)transaction;
                update.CommandText = """
                    UPDATE workout_sets SET weight_kg=$weightKg, reps=$reps, rir=$rir,
                        pain=$pain, updated_at=$updatedAt WHERE id=$id;
                    """;
                update.Parameters.AddWithValue("$weightKg", weightKg is null ? DBNull.Value : weightKg.Value);
                update.Parameters.AddWithValue("$reps", reps is null ? DBNull.Value : reps.Value);
                update.Parameters.AddWithValue("$rir", rir is null ? DBNull.Value : rir.Value);
                update.Parameters.AddWithValue("$pain", pain ? 1 : 0);
                update.Parameters.AddWithValue("$updatedAt", now.ToString("O"));
                update.Parameters.AddWithValue("$id", setId.Value.ToString("D"));
                await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            var saved = await GetSetAsync(connection, (SqliteTransaction)transaction, setId.Value, cancellationToken).ConfigureAwait(false);
            await EnqueueOutboxAsync(connection, (SqliteTransaction)transaction, "workout_set", setId.Value,
                "upsert", $"set:{setId.Value:D}:update:{now.ToUnixTimeMilliseconds()}",
                CreateWorkoutSetOutboxPayload(saved!), cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return saved;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<SavedSetData?> UpdateHistoricalLastSetAsync(
        Guid sessionId,
        decimal? weightKg,
        int? reps,
        int? rir,
        bool pain,
        CancellationToken cancellationToken = default)
    {
        if (weightKg < 0 || reps < 0 || rir is < 0 or > 10)
            throw new ArgumentOutOfRangeException(nameof(weightKg), "重量、次数或 RIR 无效。");
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Guid? setId = null;
            await using (var find = connection.CreateCommand())
            {
                find.Transaction = (SqliteTransaction)transaction;
                find.CommandText = """
                    SELECT ws.id FROM workout_sets ws
                    JOIN workout_sessions s ON s.id=ws.session_id
                    WHERE ws.session_id=$sessionId AND ws.deleted_at IS NULL AND s.deleted_at IS NULL
                    ORDER BY ws.completed_at DESC LIMIT 1;
                    """;
                find.Parameters.AddWithValue("$sessionId", sessionId.ToString("D"));
                if (await find.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string value) setId = Guid.Parse(value);
            }
            if (setId is null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }
            var now = DateTimeOffset.UtcNow;
            await using (var update = connection.CreateCommand())
            {
                update.Transaction = (SqliteTransaction)transaction;
                update.CommandText = """
                    UPDATE workout_sets SET weight_kg=$weight, reps=$reps, rir=$rir, pain=$pain,
                        updated_at=$now WHERE id=$id;
                    """;
                update.Parameters.AddWithValue("$weight", weightKg is null ? DBNull.Value : weightKg.Value);
                update.Parameters.AddWithValue("$reps", reps is null ? DBNull.Value : reps.Value);
                update.Parameters.AddWithValue("$rir", rir is null ? DBNull.Value : rir.Value);
                update.Parameters.AddWithValue("$pain", pain ? 1 : 0);
                update.Parameters.AddWithValue("$now", now.ToString("O"));
                update.Parameters.AddWithValue("$id", setId.Value.ToString("D"));
                await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            var saved = await GetSetAsync(connection, (SqliteTransaction)transaction, setId.Value, cancellationToken).ConfigureAwait(false);
            await EnqueueOutboxAsync(connection, (SqliteTransaction)transaction, "workout_set", setId.Value,
                "upsert", $"set:{setId.Value:D}:history-update:{now.ToUnixTimeMilliseconds()}",
                CreateWorkoutSetOutboxPayload(saved!), cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return saved;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task CompleteWorkoutAsync(Guid sessionId, bool endedEarly, CancellationToken cancellationToken = default)
    {
        var completedAt = DateTimeOffset.UtcNow;
        var status = endedEarly ? "interrupted" : "completed";
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                UPDATE workout_sessions SET status=$status, completed_at=$completedAt,
                    ended_early=$endedEarly, updated_at=$completedAt
                WHERE id=$id AND status='active' AND deleted_at IS NULL;
                """;
            command.Parameters.AddWithValue("$status", status);
            command.Parameters.AddWithValue("$completedAt", completedAt.ToString("O"));
            command.Parameters.AddWithValue("$endedEarly", endedEarly ? 1 : 0);
            command.Parameters.AddWithValue("$id", sessionId.ToString("D"));
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException("训练会话不存在或已经结束。");
            }

            await EnqueueOutboxAsync(connection, (SqliteTransaction)transaction, "workout_session", sessionId,
                "upsert", $"session:{sessionId:D}:complete", new
                {
                    id = sessionId,
                    status = endedEarly ? "ENDED_EARLY" : "COMPLETED",
                    completed_at = completedAt,
                    metadata = new { ended_early = endedEarly }
                }, cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task MarkTodayAsync(string kind, DateOnly? localDate = null, CancellationToken cancellationToken = default)
    {
        var normalized = kind.Trim().ToLowerInvariant();
        if (normalized is not ("rest" or "cardio"))
        {
            throw new ArgumentException("今日标记只能是 rest 或 cardio。", nameof(kind));
        }

        var date = localDate ?? DateOnly.FromDateTime(DateTime.Today);
        var now = DateTimeOffset.UtcNow.ToString("O");
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO day_marks(local_date, kind, created_at, updated_at)
            VALUES ($localDate, $kind, $now, $now)
            ON CONFLICT(local_date) DO UPDATE SET kind=excluded.kind, updated_at=excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$localDate", date.ToString("yyyy-MM-dd"));
        command.Parameters.AddWithValue("$kind", normalized);
        command.Parameters.AddWithValue("$now", now);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<WorkoutHistoryRow>> GetHistoryAsync(
        DateOnly? from = null,
        DateOnly? to = null,
        string? dayCode = null,
        string? exercise = null,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        var rows = new List<WorkoutHistoryRow>();
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        var filters = new List<string> { "s.deleted_at IS NULL", "s.status <> 'active'" };
        if (from is not null)
        {
            filters.Add("s.local_date >= $from");
            command.Parameters.AddWithValue("$from", from.Value.ToString("yyyy-MM-dd"));
        }
        if (to is not null)
        {
            filters.Add("s.local_date <= $to");
            command.Parameters.AddWithValue("$to", to.Value.ToString("yyyy-MM-dd"));
        }
        if (!string.IsNullOrWhiteSpace(dayCode))
        {
            filters.Add("s.day_code = $dayCode COLLATE NOCASE");
            command.Parameters.AddWithValue("$dayCode", dayCode.Trim());
        }
        if (!string.IsNullOrWhiteSpace(exercise))
        {
            filters.Add("EXISTS (SELECT 1 FROM workout_sets fx WHERE fx.session_id=s.id AND fx.deleted_at IS NULL AND fx.exercise_name LIKE $exercise ESCAPE '\\')");
            var escaped = exercise.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
            command.Parameters.AddWithValue("$exercise", $"%{escaped}%");
        }

        command.CommandText = $"""
            SELECT s.id, s.local_date, s.day_code, s.source, s.status,
                COUNT(ws.id), COALESCE(SUM(COALESCE(ws.weight_kg,0) * COALESCE(ws.reps,0)),0),
                COALESCE(MAX(ws.weight_kg),0), COALESCE(SUM(ws.reps),0),
                CASE WHEN EXISTS(SELECT 1 FROM outbox o WHERE o.entity_id=s.id AND o.processed_at IS NULL)
                     THEN 'pending' ELSE 'synced' END,
                s.plan_snapshot_json,
                COALESCE(GROUP_CONCAT(DISTINCT ws.exercise_name), '')
            FROM workout_sessions s
            LEFT JOIN workout_sets ws ON ws.session_id=s.id AND ws.deleted_at IS NULL
            WHERE {string.Join(" AND ", filters)}
            GROUP BY s.id
            ORDER BY s.local_date DESC, s.started_at DESC
            {(limit is > 0 ? "LIMIT $limit" : string.Empty)};
            """;
        if (limit is > 0)
        {
            command.Parameters.AddWithValue("$limit", limit.Value);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var plan = DeserializePlan(reader.GetString(10));
            rows.Add(new WorkoutHistoryRow(
                Guid.Parse(reader.GetString(0)),
                DateOnly.ParseExact(reader.GetString(1), "yyyy-MM-dd"),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt32(5),
                Convert.ToDecimal(reader.GetDouble(6)),
                reader.GetString(9),
                $"{plan.Name} v{plan.Version}",
                reader.GetString(11),
                Convert.ToDecimal(reader.GetDouble(7)),
                reader.GetInt32(8)));
        }
        return rows;
    }

    public async Task SoftDeleteWorkoutAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var deletedAt = DateTimeOffset.UtcNow;
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using (var session = connection.CreateCommand())
            {
                session.Transaction = (SqliteTransaction)transaction;
                session.CommandText = "UPDATE workout_sessions SET deleted_at=$deletedAt, updated_at=$deletedAt WHERE id=$id AND deleted_at IS NULL;";
                session.Parameters.AddWithValue("$deletedAt", deletedAt.ToString("O"));
                session.Parameters.AddWithValue("$id", sessionId.ToString("D"));
                await session.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await using (var sets = connection.CreateCommand())
            {
                sets.Transaction = (SqliteTransaction)transaction;
                sets.CommandText = "UPDATE workout_sets SET deleted_at=$deletedAt, updated_at=$deletedAt WHERE session_id=$id AND deleted_at IS NULL;";
                sets.Parameters.AddWithValue("$deletedAt", deletedAt.ToString("O"));
                sets.Parameters.AddWithValue("$id", sessionId.ToString("D"));
                await sets.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await EnqueueOutboxAsync(connection, (SqliteTransaction)transaction, "workout_session", sessionId,
                "delete", $"session:{sessionId:D}:delete", new { id = sessionId, deleted_at = deletedAt }, cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<IReadOnlyList<ExerciseSetHistoryData>> GetExactExerciseHistoryAsync(
        Guid exerciseId,
        Guid optionId,
        string equipment,
        CancellationToken cancellationToken = default)
    {
        if (exerciseId == Guid.Empty || optionId == Guid.Empty)
            throw new ArgumentException("动作和处方选项 UUID 不能为空。");
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        Guid? latestSessionId = null;
        await using (var latest = connection.CreateCommand())
        {
            latest.CommandText = """
                SELECT ws.session_id FROM workout_sets ws
                JOIN workout_sessions s ON s.id=ws.session_id
                WHERE ws.exercise_id=$exerciseId AND ws.option_id=$optionId
                  AND ws.equipment_key=$equipment AND ws.is_warmup=0
                  AND ws.deleted_at IS NULL AND s.deleted_at IS NULL AND s.status <> 'active'
                ORDER BY ws.completed_at DESC LIMIT 1;
                """;
            latest.Parameters.AddWithValue("$exerciseId", exerciseId.ToString("D"));
            latest.Parameters.AddWithValue("$optionId", optionId.ToString("D"));
            latest.Parameters.AddWithValue("$equipment", equipment ?? string.Empty);
            if (await latest.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string value)
                latestSessionId = Guid.Parse(value);
        }
        if (latestSessionId is null) return [];

        var result = new List<ExerciseSetHistoryData>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT session_id, exercise_id, option_id, equipment_key, weight_kg, reps, rir, pain, completed_at
            FROM workout_sets
            WHERE session_id=$sessionId AND exercise_id=$exerciseId AND option_id=$optionId
              AND equipment_key=$equipment AND is_warmup=0 AND deleted_at IS NULL
            ORDER BY completed_at, set_number;
            """;
        command.Parameters.AddWithValue("$sessionId", latestSessionId.Value.ToString("D"));
        command.Parameters.AddWithValue("$exerciseId", exerciseId.ToString("D"));
        command.Parameters.AddWithValue("$optionId", optionId.ToString("D"));
        command.Parameters.AddWithValue("$equipment", equipment ?? string.Empty);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new ExerciseSetHistoryData(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)),
                Guid.Parse(reader.GetString(2)), reader.GetString(3), reader.IsDBNull(4) ? null : Convert.ToDecimal(reader.GetDouble(4)),
                reader.IsDBNull(5) ? null : reader.GetInt32(5), reader.IsDBNull(6) ? null : reader.GetInt32(6),
                reader.GetInt32(7) != 0, DateTimeOffset.Parse(reader.GetString(8))));
        }
        return result;
    }

    private static async Task<IReadOnlyList<SavedSetData>> GetSetsAsync(
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
            FROM workout_sets WHERE session_id=$sessionId AND deleted_at IS NULL
            ORDER BY completed_at, set_number;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(ReadSavedSet(reader));
        }
        return result;
    }

    private static async Task<SavedSetData?> GetSetAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid setId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, session_id, plan_item_id, option_id, set_number, weight_kg, reps,
                duration_seconds, rir, pain, notes, completed_at, exercise_id, equipment_key, is_warmup,
                equipment_id, entity_version, deleted_at
            FROM workout_sets WHERE id=$id AND deleted_at IS NULL;
            """;
        command.Parameters.AddWithValue("$id", setId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadSavedSet(reader) : null;
    }

    private static SavedSetData ReadSavedSet(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        Guid.Parse(reader.GetString(1)),
        Guid.Parse(reader.GetString(2)),
        Guid.Parse(reader.GetString(3)),
        reader.GetInt32(4),
        reader.IsDBNull(5) ? null : Convert.ToDecimal(reader.GetDouble(5)),
        reader.IsDBNull(6) ? null : reader.GetInt32(6),
        reader.IsDBNull(7) ? null : reader.GetInt32(7),
        reader.IsDBNull(8) ? null : reader.GetInt32(8),
        reader.GetInt32(9) != 0,
        reader.GetString(10),
        DateTimeOffset.Parse(reader.GetString(11)),
        reader.IsDBNull(12) ? Guid.Empty : Guid.Parse(reader.GetString(12)),
        reader.FieldCount <= 13 || reader.IsDBNull(13) ? string.Empty : reader.GetString(13),
        reader.FieldCount > 14 && reader.GetInt32(14) != 0,
        reader.FieldCount <= 15 || reader.IsDBNull(15) ? null : Guid.Parse(reader.GetString(15)),
        reader.FieldCount <= 16 || reader.IsDBNull(16) ? 0 : reader.GetInt64(16),
        reader.FieldCount <= 17 || reader.IsDBNull(17) ? null : DateTimeOffset.Parse(reader.GetString(17)));

    private static object CreateWorkoutSetOutboxPayload(SavedSetData saved) => new
    {
        id = saved.Id,
        session_id = saved.SessionId,
        plan_slot_id = saved.PlanItemId,
        source_plan_slot_option_id = saved.OptionId,
        exercise_id = saved.ExerciseId,
        equipment_id = saved.EquipmentId,
        equipment = saved.Equipment,
        set_number = saved.SetNumber,
        weight_kg = saved.WeightKg,
        reps = saved.Reps,
        duration_seconds = saved.DurationSeconds,
        is_warmup = saved.IsWarmup,
        rir = saved.Rir,
        pain = saved.Pain,
        notes = saved.Notes,
        completed = true,
        completed_at = saved.CompletedAt
    };

    private async Task<int?> GetEffectiveSetCapAsync(
        PlanData plan,
        DateOnly localDate,
        CancellationToken cancellationToken)
    {
        if (plan.DeloadWeeks <= 0 || plan.DeloadMaxSets <= 0)
        {
            return null;
        }

        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COALESCE(start_local_date, substr(assigned_at, 1, 10)) FROM plan_assignments
            WHERE plan_version_id=$planVersionId AND is_active=1
            ORDER BY assigned_at DESC LIMIT 1;
            """;
        command.Parameters.AddWithValue("$planVersionId", plan.Id.ToString("D"));
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not string assignedAtText)
        {
            return null;
        }

        var assignedDate = DateOnly.ParseExact(assignedAtText, "yyyy-MM-dd");
        var elapsedDays = localDate.DayNumber - assignedDate.DayNumber;
        return elapsedDays >= 0 && elapsedDays < plan.DeloadWeeks * 7 ? plan.DeloadMaxSets : null;
    }

    private static PlanData ApplySetCap(PlanData plan, int? setCap)
    {
        if (setCap is null)
        {
            return plan;
        }

        var days = plan.Days.Select(day => day with
        {
            Items = day.Items.Select(item => item with
            {
                Options = item.Options.Select(option => option with { Sets = Math.Min(option.Sets, setCap.Value) }).ToArray()
            }).ToArray()
        }).ToArray();
        return plan with { Days = days };
    }
}
