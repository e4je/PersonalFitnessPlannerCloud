using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using PersonalFitnessPlanner.Contracts;
using PersonalFitnessPlanner.Infrastructure.Models;

namespace PersonalFitnessPlanner.Infrastructure.Persistence;

public sealed partial class FitnessRepository
{
    public Task<int> ApplyBootstrapAsync(string bootstrapJson, CancellationToken cancellationToken = default) =>
        ApplyBootstrapCoreAsync(bootstrapJson, resetServerCaches: false, cancellationToken);

    public Task<int> ApplyFullBootstrapAsync(string bootstrapJson, CancellationToken cancellationToken = default) =>
        ApplyBootstrapCoreAsync(bootstrapJson, resetServerCaches: true, cancellationToken);

    private async Task<int> ApplyBootstrapCoreAsync(
        string bootstrapJson,
        bool resetServerCaches,
        CancellationToken cancellationToken)
    {
        var bootstrap = ContractJson.Deserialize<BootstrapDto>(bootstrapJson)
            ?? throw new InvalidDataException("服务器 bootstrap 响应为空。");
        var equipmentNames = bootstrap.Equipment.ToDictionary(x => x.Id, x => x.Name);
        var exerciseDtos = bootstrap.Exercises.ToDictionary(x => x.Id);
        var exerciseModels = bootstrap.Exercises.ToDictionary(
            x => x.Id,
            x => ToExerciseLibraryItem(x, equipmentNames, bootstrap.Exercises));
        var planVersionDtos = bootstrap.PlanVersions.ToList();
        if (bootstrap.CurrentPlan is not null && planVersionDtos.All(x => x.Id != bootstrap.CurrentPlan.Id))
            planVersionDtos.Add(bootstrap.CurrentPlan);
        var count = bootstrap.Equipment.Count + bootstrap.Exercises.Count + planVersionDtos.Count + bootstrap.Assignments.Count +
                    bootstrap.WorkoutSessions.Count + bootstrap.Readiness.Count + bootstrap.CardioSessions.Count +
                    (bootstrap.User is null ? 0 : 1);

        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var accountSubject = await ReadAccountSubjectAsync(
                connection, (SqliteTransaction)transaction, cancellationToken).ConfigureAwait(false);
            if (bootstrap.User is not null && accountSubject is not null &&
                !string.Equals(accountSubject, bootstrap.User.Id.ToString("D"), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"bootstrap 用户 {bootstrap.User.Id:D} 与本地缓存账号 {accountSubject} 不一致。");
            }

            if (resetServerCaches)
            {
                var now = DateTimeOffset.UtcNow.ToString("O");
                await using var reset = connection.CreateCommand();
                reset.Transaction = (SqliteTransaction)transaction;
                reset.CommandText = """
                    UPDATE plans SET deleted_at=$now, updated_at=$now
                    WHERE status <> 'draft' AND deleted_at IS NULL
                      AND NOT EXISTS(SELECT 1 FROM outbox o WHERE o.entity_id=plans.id AND o.processed_at IS NULL);
                    UPDATE exercises SET deleted_at=$now, updated_at=$now
                    WHERE status <> 'draft' AND deleted_at IS NULL
                      AND NOT EXISTS(SELECT 1 FROM outbox o WHERE o.entity_id=exercises.id AND o.processed_at IS NULL);
                    UPDATE equipment_cache SET deleted_at=$now WHERE deleted_at IS NULL;
                    UPDATE plan_assignments SET is_active=0
                    WHERE NOT EXISTS(SELECT 1 FROM outbox o WHERE o.entity_id=plan_assignments.id AND o.processed_at IS NULL);
                    UPDATE user_cache SET deleted_at=$now, updated_at=$now
                    WHERE deleted_at IS NULL
                      AND NOT EXISTS(SELECT 1 FROM outbox o WHERE o.entity_id=user_cache.id AND o.processed_at IS NULL);
                    UPDATE workout_sets SET deleted_at=$now, updated_at=$now
                    WHERE deleted_at IS NULL
                      AND NOT EXISTS(SELECT 1 FROM outbox o WHERE o.entity_id=workout_sets.id AND o.processed_at IS NULL);
                    UPDATE workout_sessions SET deleted_at=$now, updated_at=$now
                    WHERE deleted_at IS NULL
                      AND NOT EXISTS(
                        SELECT 1 FROM outbox o
                        WHERE o.processed_at IS NULL
                          AND (o.entity_id=workout_sessions.id OR (
                            o.entity_type='workout_set' AND EXISTS(
                              SELECT 1 FROM workout_sets ws
                              WHERE ws.id=o.entity_id AND ws.session_id=workout_sessions.id
                            )
                          ))
                      );
                    UPDATE daily_readiness SET deleted_at=$now, updated_at=$now
                    WHERE deleted_at IS NULL
                      AND NOT EXISTS(SELECT 1 FROM outbox o WHERE o.entity_id=daily_readiness.id AND o.processed_at IS NULL);
                    UPDATE cardio_sessions SET deleted_at=$now, updated_at=$now
                    WHERE deleted_at IS NULL
                      AND NOT EXISTS(SELECT 1 FROM outbox o WHERE o.entity_id=cardio_sessions.id AND o.processed_at IS NULL);
                    """;
                reset.Parameters.AddWithValue("$now", now);
                await reset.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            if (bootstrap.User is not null)
                await UpsertUserAsync(connection, (SqliteTransaction)transaction, bootstrap.User, cancellationToken)
                    .ConfigureAwait(false);

            foreach (var equipment in bootstrap.Equipment)
                await UpsertEquipmentAsync(connection, (SqliteTransaction)transaction, equipment, cancellationToken).ConfigureAwait(false);
            foreach (var exercise in bootstrap.Exercises)
                await UpsertExerciseAsync(connection, (SqliteTransaction)transaction, exercise, equipmentNames,
                    bootstrap.Exercises, cancellationToken).ConfigureAwait(false);

            var planModels = new Dictionary<Guid, PlanData>();
            foreach (var planVersion in planVersionDtos)
            {
                var plan = ConvertPlan(planVersion, exerciseModels, equipmentNames);
                planModels[plan.Id] = plan;
                await UpsertServerPlanAsync(connection, (SqliteTransaction)transaction, plan, cancellationToken,
                    planVersion.DeletedAt, planVersion.Version).ConfigureAwait(false);
            }
            var currentPlan = bootstrap.CurrentPlan is null
                ? null
                : planModels.GetValueOrDefault(bootstrap.CurrentPlan.Id);

            if (bootstrap.Assignments.Count > 0)
            {
                await using var deactivate = connection.CreateCommand();
                deactivate.Transaction = (SqliteTransaction)transaction;
                deactivate.CommandText = "UPDATE plan_assignments SET is_active=0 WHERE is_active=1;";
                await deactivate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                foreach (var assignment in bootstrap.Assignments.OrderBy(x => x.StartLocalDate))
                    await UpsertAssignmentAsync(connection, (SqliteTransaction)transaction, assignment, cancellationToken).ConfigureAwait(false);
            }

            if (currentPlan is not null && bootstrap.CurrentPlan?.DeletedAt is null &&
                !bootstrap.Assignments.Any(assignment => assignment.IsActive && assignment.DeletedAt is null))
            {
                await UpsertLocalFallbackAssignmentAsync(
                    connection,
                    (SqliteTransaction)transaction,
                    bootstrap.User?.Id ?? Guid.Empty,
                    currentPlan.Id,
                    cancellationToken).ConfigureAwait(false);
            }

            foreach (var workout in bootstrap.WorkoutSessions)
            {
                if (await HasPendingWorkoutAsync(
                        connection, (SqliteTransaction)transaction, workout.Id, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                var session = ConvertWorkout(workout, currentPlan, exerciseModels, equipmentNames);
                await UpsertServerSessionAsync(connection, (SqliteTransaction)transaction, session, cancellationToken).ConfigureAwait(false);
            }

            foreach (var readiness in bootstrap.Readiness)
            {
                if (await HasPendingEntityAsync(
                        connection, (SqliteTransaction)transaction, readiness.Id,
                        "daily_readiness", "readiness", cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                await UpsertServerReadinessAsync(connection, (SqliteTransaction)transaction, readiness, cancellationToken)
                    .ConfigureAwait(false);
            }
            foreach (var cardio in bootstrap.CardioSessions)
            {
                if (await HasPendingEntityAsync(
                        connection, (SqliteTransaction)transaction, cardio.Id,
                        "cardio_session", "cardio", cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                await UpsertServerCardioAsync(connection, (SqliteTransaction)transaction, cardio, cancellationToken)
                    .ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return count;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task UpsertUserAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        UserDto user,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO user_cache(id, email, display_name, timezone, weight_unit, payload_json,
                entity_version, updated_at, deleted_at)
            VALUES ($id, $email, $displayName, $timezone, $weightUnit, $payload, $version,
                $updatedAt, $deletedAt)
            ON CONFLICT(id) DO UPDATE SET email=excluded.email, display_name=excluded.display_name,
                timezone=excluded.timezone, weight_unit=excluded.weight_unit,
                payload_json=excluded.payload_json, entity_version=excluded.entity_version,
                updated_at=excluded.updated_at, deleted_at=excluded.deleted_at
            WHERE excluded.entity_version >= user_cache.entity_version;
            """;
        command.Parameters.AddWithValue("$id", user.Id.ToString("D"));
        command.Parameters.AddWithValue("$email", user.Email);
        command.Parameters.AddWithValue("$displayName", user.DisplayName);
        command.Parameters.AddWithValue("$timezone", string.IsNullOrWhiteSpace(user.Timezone) ? "UTC" : user.Timezone);
        command.Parameters.AddWithValue("$weightUnit", string.IsNullOrWhiteSpace(user.WeightUnit) ? "KG" : user.WeightUnit);
        command.Parameters.AddWithValue("$payload", ContractJson.Serialize(user));
        command.Parameters.AddWithValue("$version", user.Version);
        command.Parameters.AddWithValue("$updatedAt", (user.UpdatedAt ?? DateTimeOffset.UtcNow).ToString("O"));
        command.Parameters.AddWithValue("$deletedAt", user.DeletedAt is null
            ? DBNull.Value
            : user.DeletedAt.Value.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpsertServerReadinessAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ReadinessDto readiness,
        CancellationToken cancellationToken)
    {
        var updatedAt = readiness.UpdatedAt ?? readiness.CreatedAt ?? DateTimeOffset.UtcNow;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO daily_readiness(id, local_date, fatigue_score, sleep_quality, pain_notes,
                notes, created_at, updated_at, deleted_at, entity_version)
            VALUES ($id, $localDate, $fatigue, $sleep, $pain, $notes, $createdAt, $updatedAt,
                $deletedAt, $version)
            ON CONFLICT DO UPDATE SET id=excluded.id, local_date=excluded.local_date,
                fatigue_score=excluded.fatigue_score, sleep_quality=excluded.sleep_quality,
                pain_notes=excluded.pain_notes, notes=excluded.notes, updated_at=excluded.updated_at,
                deleted_at=excluded.deleted_at, entity_version=excluded.entity_version
            WHERE excluded.entity_version >= daily_readiness.entity_version;
            """;
        command.Parameters.AddWithValue("$id", readiness.Id.ToString("D"));
        command.Parameters.AddWithValue("$localDate", readiness.LocalDate.ToString("yyyy-MM-dd"));
        command.Parameters.AddWithValue("$fatigue", readiness.FatigueScore);
        command.Parameters.AddWithValue("$sleep", readiness.SleepQuality is null ? DBNull.Value : readiness.SleepQuality.Value);
        command.Parameters.AddWithValue("$pain", readiness.PainNotes ?? string.Empty);
        command.Parameters.AddWithValue("$notes", readiness.Notes ?? string.Empty);
        command.Parameters.AddWithValue("$createdAt", (readiness.CreatedAt ?? updatedAt).ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", updatedAt.ToString("O"));
        command.Parameters.AddWithValue("$deletedAt", readiness.DeletedAt is null
            ? DBNull.Value
            : readiness.DeletedAt.Value.ToString("O"));
        command.Parameters.AddWithValue("$version", readiness.Version);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpsertServerCardioAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CardioSessionDto cardio,
        CancellationToken cancellationToken)
    {
        var updatedAt = cardio.UpdatedAt ?? cardio.CreatedAt ?? DateTimeOffset.UtcNow;
        var durationMinutes = cardio.DurationMinutes > 0
            ? cardio.DurationMinutes
            : Math.Max(1, (int)Math.Round((cardio.DurationSeconds ?? 60) / 60d));
        var distanceKm = cardio.DistanceKm ?? (cardio.DistanceMeters is null ? null : cardio.DistanceMeters / 1_000d);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO cardio_sessions(id, local_date, activity, duration_minutes, distance_km,
                started_at, completed_at, notes, created_at, updated_at, deleted_at, source,
                entity_version)
            VALUES ($id, $localDate, $activity, $duration, $distance, $startedAt, $completedAt,
                $notes, $createdAt, $updatedAt, $deletedAt, $source, $version)
            ON CONFLICT(id) DO UPDATE SET local_date=excluded.local_date, activity=excluded.activity,
                duration_minutes=excluded.duration_minutes, distance_km=excluded.distance_km,
                started_at=excluded.started_at, completed_at=excluded.completed_at,
                notes=excluded.notes, updated_at=excluded.updated_at, deleted_at=excluded.deleted_at,
                source=excluded.source, entity_version=excluded.entity_version
            WHERE excluded.entity_version >= cardio_sessions.entity_version;
            """;
        command.Parameters.AddWithValue("$id", cardio.Id.ToString("D"));
        command.Parameters.AddWithValue("$localDate", cardio.LocalDate.ToString("yyyy-MM-dd"));
        command.Parameters.AddWithValue("$activity", string.IsNullOrWhiteSpace(cardio.Activity)
            ? cardio.ActivityType ?? string.Empty
            : cardio.Activity);
        command.Parameters.AddWithValue("$duration", durationMinutes);
        command.Parameters.AddWithValue("$distance", distanceKm is null ? DBNull.Value : distanceKm.Value);
        command.Parameters.AddWithValue("$startedAt", cardio.StartedAt.ToString("O"));
        command.Parameters.AddWithValue("$completedAt", cardio.CompletedAt is null
            ? DBNull.Value
            : cardio.CompletedAt.Value.ToString("O"));
        command.Parameters.AddWithValue("$notes", cardio.Notes ?? string.Empty);
        command.Parameters.AddWithValue("$createdAt", (cardio.CreatedAt ?? cardio.StartedAt).ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", updatedAt.ToString("O"));
        command.Parameters.AddWithValue("$deletedAt", cardio.DeletedAt is null
            ? DBNull.Value
            : cardio.DeletedAt.Value.ToString("O"));
        command.Parameters.AddWithValue("$source", !string.IsNullOrWhiteSpace(cardio.Source)
            ? cardio.Source.Trim().ToLowerInvariant()
            : !string.IsNullOrWhiteSpace(cardio.SourceDevice)
                ? cardio.SourceDevice.Trim().ToLowerInvariant()
                : "cloud");
        command.Parameters.AddWithValue("$version", cardio.Version);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<(IReadOnlyDictionary<Guid, ExerciseLibraryItem> Exercises, IReadOnlyDictionary<Guid, string> Equipment)>
        LoadCloudCatalogAsync(CancellationToken cancellationToken)
    {
        var exercises = (await GetExercisesAsync(cancellationToken).ConfigureAwait(false)).ToDictionary(x => x.Id);
        var equipment = new Dictionary<Guid, string>();
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name FROM equipment_cache WHERE deleted_at IS NULL;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            equipment[Guid.Parse(reader.GetString(0))] = reader.GetString(1);
        return (exercises, equipment);
    }

    private static async Task UpsertEquipmentAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        EquipmentDto equipment,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO equipment_cache(id, name, category, payload_json, entity_version, updated_at, deleted_at)
            VALUES ($id, $name, $category, $payload, $version, $updatedAt, $deletedAt)
            ON CONFLICT(id) DO UPDATE SET name=excluded.name, category=excluded.category,
                payload_json=excluded.payload_json, entity_version=excluded.entity_version,
                updated_at=excluded.updated_at, deleted_at=excluded.deleted_at;
            """;
        command.Parameters.AddWithValue("$id", equipment.Id.ToString("D"));
        command.Parameters.AddWithValue("$name", equipment.Name);
        command.Parameters.AddWithValue("$category", equipment.Category);
        command.Parameters.AddWithValue("$payload", ContractJson.Serialize(equipment));
        command.Parameters.AddWithValue("$version", equipment.Version);
        command.Parameters.AddWithValue("$updatedAt", (equipment.UpdatedAt ?? DateTimeOffset.UtcNow).ToString("O"));
        command.Parameters.AddWithValue("$deletedAt", equipment.DeletedAt is null ? DBNull.Value : equipment.DeletedAt.Value.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpsertExerciseAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ExerciseDto exercise,
        IReadOnlyDictionary<Guid, string> equipmentNames,
        IReadOnlyList<ExerciseDto> allExercises,
        CancellationToken cancellationToken)
    {
        var model = ToExerciseLibraryItem(exercise, equipmentNames, allExercises);
        var now = (exercise.UpdatedAt ?? DateTimeOffset.UtcNow).ToString("O");
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO exercises(id, name, body_part, equipment, prescription, cues,
                common_mistakes, alternatives, version, status, created_at, updated_at, deleted_at, equipment_id)
            VALUES ($id, $name, $bodyPart, $equipment, $prescription, $cues, $mistakes,
                $alternatives, $version, 'published', $createdAt, $updatedAt, $deletedAt, $equipmentId)
            ON CONFLICT(id) DO UPDATE SET name=excluded.name, body_part=excluded.body_part,
                equipment=excluded.equipment, prescription=excluded.prescription, cues=excluded.cues,
                common_mistakes=excluded.common_mistakes, alternatives=excluded.alternatives,
                version=excluded.version, status='published', updated_at=excluded.updated_at,
                deleted_at=excluded.deleted_at, equipment_id=excluded.equipment_id;
            """;
        command.Parameters.AddWithValue("$id", model.Id.ToString("D"));
        command.Parameters.AddWithValue("$name", model.Name);
        command.Parameters.AddWithValue("$bodyPart", model.BodyPart);
        command.Parameters.AddWithValue("$equipment", model.Equipment);
        command.Parameters.AddWithValue("$prescription", model.Prescription);
        command.Parameters.AddWithValue("$cues", model.Cues);
        command.Parameters.AddWithValue("$mistakes", model.CommonMistakes);
        command.Parameters.AddWithValue("$alternatives", model.Alternatives);
        command.Parameters.AddWithValue("$version", model.Version);
        command.Parameters.AddWithValue("$createdAt", (exercise.CreatedAt ?? exercise.UpdatedAt ?? DateTimeOffset.UtcNow).ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", now);
        command.Parameters.AddWithValue("$deletedAt", exercise.DeletedAt is null ? DBNull.Value : exercise.DeletedAt.Value.ToString("O"));
        command.Parameters.AddWithValue("$equipmentId", exercise.EquipmentId is null ? DBNull.Value : exercise.EquipmentId.Value.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpsertAssignmentAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PlanAssignmentDto assignment,
        CancellationToken cancellationToken)
    {
        await using var exists = connection.CreateCommand();
        exists.Transaction = transaction;
        exists.CommandText = "SELECT COUNT(*) FROM plans WHERE id=$id;";
        exists.Parameters.AddWithValue("$id", assignment.PlanVersionId.ToString("D"));
        if (Convert.ToInt32(await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) == 0) return;

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO plan_assignments(id, plan_version_id, assigned_at, is_active, start_local_date)
            VALUES ($id, $planVersionId, $assignedAt, $active, $startDate)
            ON CONFLICT(id) DO UPDATE SET plan_version_id=excluded.plan_version_id,
                assigned_at=excluded.assigned_at, is_active=excluded.is_active,
                start_local_date=excluded.start_local_date;
            """;
        command.Parameters.AddWithValue("$id", assignment.Id.ToString("D"));
        command.Parameters.AddWithValue("$planVersionId", assignment.PlanVersionId.ToString("D"));
        command.Parameters.AddWithValue("$assignedAt", (assignment.CreatedAt ?? assignment.UpdatedAt ?? DateTimeOffset.UtcNow).ToString("O"));
        command.Parameters.AddWithValue("$active", assignment.IsActive && assignment.DeletedAt is null ? 1 : 0);
        command.Parameters.AddWithValue("$startDate", assignment.StartLocalDate.ToString("yyyy-MM-dd"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpsertLocalFallbackAssignmentAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid userId,
        Guid planVersionId,
        CancellationToken cancellationToken)
    {
        await using (var pending = connection.CreateCommand())
        {
            pending.Transaction = transaction;
            pending.CommandText = """
                SELECT COUNT(*)
                FROM plan_assignments a
                JOIN outbox o ON o.entity_id=a.id
                WHERE a.is_active=1 AND o.processed_at IS NULL;
                """;
            if (Convert.ToInt32(await pending.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) > 0)
            {
                return;
            }
        }

        var now = DateTimeOffset.UtcNow;
        var assignmentId = CreateStableLocalId($"local-assignment:{userId:D}:{planVersionId:D}");
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE plan_assignments SET is_active=0 WHERE is_active=1;
            INSERT INTO plan_assignments(id, plan_version_id, assigned_at, is_active, start_local_date)
            VALUES ($id, $planVersionId, $assignedAt, 1, $startDate)
            ON CONFLICT(id) DO UPDATE SET plan_version_id=excluded.plan_version_id,
                assigned_at=plan_assignments.assigned_at, is_active=1,
                start_local_date=COALESCE(plan_assignments.start_local_date, excluded.start_local_date);
            """;
        command.Parameters.AddWithValue("$id", assignmentId.ToString("D"));
        command.Parameters.AddWithValue("$planVersionId", planVersionId.ToString("D"));
        command.Parameters.AddWithValue("$assignedAt", now.ToString("O"));
        command.Parameters.AddWithValue("$startDate", DateOnly.FromDateTime(DateTime.Today).ToString("yyyy-MM-dd"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static Guid CreateStableLocalId(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return new Guid(hash.AsSpan(0, 16));
    }

    private static async Task<bool> HasPendingWorkoutAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS(
              SELECT 1 FROM outbox o
              WHERE o.processed_at IS NULL AND (
                (o.entity_id=$sessionId AND o.entity_type IN ('workout_session','workout_sessions')) OR
                (o.entity_type='workout_set' AND EXISTS(
                  SELECT 1 FROM workout_sets ws WHERE ws.id=o.entity_id AND ws.session_id=$sessionId
                ))
              )
            );
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString("D"));
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) != 0;
    }

    private static async Task<bool> HasPendingEntityAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid entityId,
        string canonicalType,
        string legacyType,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS(
              SELECT 1 FROM outbox
              WHERE processed_at IS NULL AND entity_id=$entityId
                AND entity_type IN ($canonicalType,$legacyType)
            );
            """;
        command.Parameters.AddWithValue("$entityId", entityId.ToString("D"));
        command.Parameters.AddWithValue("$canonicalType", canonicalType);
        command.Parameters.AddWithValue("$legacyType", legacyType);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) != 0;
    }

    private static ExerciseLibraryItem ToExerciseLibraryItem(
        ExerciseDto exercise,
        IReadOnlyDictionary<Guid, string> equipmentNames,
        IReadOnlyList<ExerciseDto> allExercises)
    {
        var nameMap = allExercises.ToDictionary(x => x.Id, x => x.Name);
        var alternatives = exercise.Alternatives
            .OrderBy(x => x.SortOrder)
            .Select(x => nameMap.GetValueOrDefault(x.AlternativeExerciseId, x.AlternativeExerciseId.ToString("D")));
        return new ExerciseLibraryItem(exercise.Id, exercise.Name, exercise.BodyPart,
            exercise.EquipmentId is { } equipmentId ? equipmentNames.GetValueOrDefault(equipmentId, string.Empty) : string.Empty,
            $"{exercise.DefaultSets}×{exercise.RepMin}～{exercise.RepMax}{(exercise.RepUnit == "seconds" ? "秒" : "次")}",
            exercise.Cues, exercise.CommonMistakes, string.Join("、", alternatives), exercise.Version, "published");
    }

    private static PlanData ConvertPlan(
        PlanVersionDto plan,
        IReadOnlyDictionary<Guid, ExerciseLibraryItem> exercises,
        IReadOnlyDictionary<Guid, string> equipmentNames)
    {
        var days = plan.Days.OrderBy(x => x.SortOrder).Select(day => new PlanDayData(day.Code, day.Name,
            day.Slots.OrderBy(x => x.Position).Select(slot => new PlanItemData(slot.Id, slot.Position,
                slot.BodyPart, slot.Cues, slot.CommonMistakes,
                slot.Options.OrderBy(x => x.SortOrder).Select(option =>
                {
                    exercises.TryGetValue(option.ExerciseId, out var exercise);
                    var equipment = option.EquipmentId is { } equipmentId
                        ? equipmentNames.GetValueOrDefault(equipmentId, exercise?.Equipment ?? string.Empty)
                        : exercise?.Equipment ?? string.Empty;
                    return new ExerciseOptionData(option.Id, option.ExerciseId,
                        exercise?.Name ?? option.ExerciseId.ToString("D"), equipment, option.IsPreferred,
                        option.SetCount, option.RepMin, option.RepMax, option.RepUnit, option.RestSeconds,
                        option.EquipmentId);
                }).ToArray(), slot.SeatPosition, slot.BenchAngle, slot.MachineNumber)).ToArray())).ToArray();
        return new PlanData(plan.Id, plan.PlanId, plan.PlanName, plan.VersionNumber,
            plan.Status.ToLowerInvariant(), plan.IntroWeeks, plan.IntroMaxSets, days, plan.PublishedAt,
            plan.WeeklyFrequency, plan.MinRestDays, plan.FatigueThreshold);
    }

    private static WorkoutExportSession ConvertWorkout(
        WorkoutSessionDto workout,
        PlanData? currentPlan,
        IReadOnlyDictionary<Guid, ExerciseLibraryItem> exercises,
        IReadOnlyDictionary<Guid, string> equipmentNames)
    {
        PlanData? snapshot = null;
        if (!string.IsNullOrWhiteSpace(workout.PlanSnapshotJson))
        {
            try
            {
                var localSnapshot = DeserializePlan(workout.PlanSnapshotJson);
                if (localSnapshot.Id != Guid.Empty && localSnapshot.Days is { Count: > 0 }) snapshot = localSnapshot;
            }
            catch (Exception exception) when (exception is JsonException or InvalidDataException) { }
            if (snapshot is null)
            {
                try
                {
                    var dto = ContractJson.Deserialize<PlanVersionDto>(workout.PlanSnapshotJson);
                    if (dto is not null && dto.Id != Guid.Empty && dto.Days.Count > 0)
                        snapshot = ConvertPlan(dto, exercises, equipmentNames);
                }
                catch (JsonException) { }
            }
        }
        snapshot ??= currentPlan ?? new PlanData(workout.PlanVersionId ?? Guid.Empty,
            Guid.Empty, "云端历史计划", 1, "published", 0, 2, []);
        var status = workout.DeletedAt is not null ? "completed" : workout.Status.ToUpperInvariant() switch
        {
            "IN_PROGRESS" or "ACTIVE" => "active",
            "ENDED_EARLY" or "INTERRUPTED" => "interrupted",
            _ => "completed"
        };
        var sets = workout.Sets.Select(set => new SavedSetData(set.Id, workout.Id,
            set.PlanSlotId ?? Guid.Empty, set.SourcePlanSlotOptionId ?? Guid.Empty, set.SetNumber,
            set.WeightKg is null ? null : Convert.ToDecimal(set.WeightKg.Value), set.Reps,
            set.DurationSeconds, set.Rir, set.Pain, set.Notes ?? string.Empty,
            set.CompletedAt ?? set.UpdatedAt ?? workout.CompletedAt ?? workout.StartedAt,
            set.ExerciseId,
            set.EquipmentId is { } equipmentId ? equipmentNames.GetValueOrDefault(equipmentId, string.Empty) : string.Empty,
            set.IsWarmup, set.EquipmentId, set.Version, set.DeletedAt)).ToArray();
        var source = !string.IsNullOrWhiteSpace(workout.Source)
            ? workout.Source.Trim().ToLowerInvariant()
            : !string.IsNullOrWhiteSpace(workout.SourceDevice)
                ? workout.SourceDevice.Trim().ToLowerInvariant()
                : "cloud";
        return new WorkoutExportSession(workout.Id, workout.PlanDayCode ?? "A", workout.LocalDate,
            status, source, workout.PlanVersionId ?? snapshot.Id, snapshot, workout.StartedAt,
            workout.CompletedAt, string.Equals(status, "interrupted", StringComparison.Ordinal),
            workout.DeletedAt, sets, workout.PlanAssignmentId, workout.PlanDayId,
            string.IsNullOrWhiteSpace(workout.Timezone) ? "UTC" : workout.Timezone, workout.Version);
    }
}
