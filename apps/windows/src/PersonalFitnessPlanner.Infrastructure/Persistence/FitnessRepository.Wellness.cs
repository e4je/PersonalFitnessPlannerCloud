using Microsoft.Data.Sqlite;
using PersonalFitnessPlanner.Infrastructure.Models;

namespace PersonalFitnessPlanner.Infrastructure.Persistence;

public sealed partial class FitnessRepository
{
    public async Task SaveReadinessAsync(DailyReadinessData readiness, CancellationToken cancellationToken = default)
    {
        if (readiness.Id == Guid.Empty)
            throw new ArgumentException("每日状态 UUID 不能为空。", nameof(readiness));
        if (readiness.FatigueScore is < 1 or > 10 || readiness.SleepQuality is < 1 or > 5)
            throw new ArgumentOutOfRangeException(nameof(readiness), "疲劳评分必须为 1-10，睡眠评分必须为 1-5。");
        var now = DateTimeOffset.UtcNow;
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO daily_readiness(id, local_date, fatigue_score, sleep_quality, pain_notes,
                    notes, created_at, updated_at, deleted_at)
                VALUES ($id, $date, $fatigue, $sleep, $pain, $notes, $now, $now, NULL)
                ON CONFLICT(local_date) DO UPDATE SET fatigue_score=excluded.fatigue_score,
                    sleep_quality=excluded.sleep_quality, pain_notes=excluded.pain_notes,
                    notes=excluded.notes, updated_at=excluded.updated_at, deleted_at=NULL
                RETURNING id;
                """;
            command.Parameters.AddWithValue("$id", readiness.Id.ToString("D"));
            command.Parameters.AddWithValue("$date", readiness.LocalDate.ToString("yyyy-MM-dd"));
            command.Parameters.AddWithValue("$fatigue", readiness.FatigueScore);
            command.Parameters.AddWithValue("$sleep", readiness.SleepQuality is null ? DBNull.Value : readiness.SleepQuality.Value);
            command.Parameters.AddWithValue("$pain", readiness.PainNotes ?? string.Empty);
            command.Parameters.AddWithValue("$notes", readiness.Notes ?? string.Empty);
            command.Parameters.AddWithValue("$now", now.ToString("O"));
            var entityIdText = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string
                ?? throw new InvalidDataException("保存每日状态后未返回实体 UUID。");
            var entityId = Guid.Parse(entityIdText);
            var payload = new
            {
                id = entityId,
                local_date = readiness.LocalDate,
                fatigue_score = readiness.FatigueScore,
                sleep_quality = readiness.SleepQuality,
                pain_notes = readiness.PainNotes,
                notes = readiness.Notes,
                metrics = new Dictionary<string, object>()
            };
            await EnqueueOutboxAsync(connection, (SqliteTransaction)transaction, "daily_readiness", entityId,
                "upsert", $"readiness:{entityId:D}:mutation:{readiness.Id:D}", payload, cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<DailyReadinessData?> GetLatestReadinessAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, local_date, fatigue_score, sleep_quality, pain_notes, notes
            FROM daily_readiness WHERE deleted_at IS NULL ORDER BY local_date DESC LIMIT 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        return new DailyReadinessData(Guid.Parse(reader.GetString(0)), DateOnly.ParseExact(reader.GetString(1), "yyyy-MM-dd"),
            reader.GetInt32(2), reader.IsDBNull(3) ? null : reader.GetInt32(3), reader.GetString(4), reader.GetString(5));
    }

    public async Task SaveCardioSessionAsync(CardioSessionData cardio, CancellationToken cancellationToken = default)
    {
        if (cardio.DurationMinutes <= 0 || cardio.DistanceKm < 0 || string.IsNullOrWhiteSpace(cardio.Activity))
            throw new ArgumentOutOfRangeException(nameof(cardio), "有氧类型、时长或距离无效。");
        var now = DateTimeOffset.UtcNow;
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO cardio_sessions(id, local_date, activity, duration_minutes, distance_km,
                    started_at, completed_at, notes, created_at, updated_at, deleted_at)
                VALUES ($id, $date, $activity, $duration, $distance, $startedAt, $completedAt,
                    $notes, $now, $now, NULL)
                ON CONFLICT(id) DO UPDATE SET local_date=excluded.local_date, activity=excluded.activity,
                    duration_minutes=excluded.duration_minutes, distance_km=excluded.distance_km,
                    completed_at=excluded.completed_at, notes=excluded.notes,
                    updated_at=excluded.updated_at, deleted_at=NULL;
                """;
            command.Parameters.AddWithValue("$id", cardio.Id.ToString("D"));
            command.Parameters.AddWithValue("$date", cardio.LocalDate.ToString("yyyy-MM-dd"));
            command.Parameters.AddWithValue("$activity", cardio.Activity.Trim());
            command.Parameters.AddWithValue("$duration", cardio.DurationMinutes);
            command.Parameters.AddWithValue("$distance", cardio.DistanceKm is null ? DBNull.Value : cardio.DistanceKm.Value);
            command.Parameters.AddWithValue("$startedAt", cardio.StartedAt.ToString("O"));
            command.Parameters.AddWithValue("$completedAt", cardio.CompletedAt is null ? DBNull.Value : cardio.CompletedAt.Value.ToString("O"));
            command.Parameters.AddWithValue("$notes", cardio.Notes ?? string.Empty);
            command.Parameters.AddWithValue("$now", now.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            var payload = new
            {
                id = cardio.Id,
                client_id = cardio.Id,
                source = "windows",
                source_device = "windows",
                local_date = cardio.LocalDate,
                activity = cardio.Activity.Trim(),
                activity_type = cardio.Activity.Trim().ToLowerInvariant(),
                duration_minutes = cardio.DurationMinutes,
                duration_seconds = checked(cardio.DurationMinutes * 60),
                distance_km = cardio.DistanceKm,
                distance_meters = cardio.DistanceKm * 1_000m,
                started_at = cardio.StartedAt,
                completed_at = cardio.CompletedAt,
                notes = cardio.Notes,
                metrics = new Dictionary<string, object>()
            };
            await EnqueueOutboxAsync(connection, (SqliteTransaction)transaction, "cardio_session", cardio.Id,
                "upsert", $"cardio:{cardio.Id:D}:{now.ToUnixTimeMilliseconds()}", payload, cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task SaveExerciseSetupPreferenceAsync(
        ExerciseSetupPreferenceData preference,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO exercise_setup_preferences(exercise_id, equipment_key, seat_position,
                bench_angle, machine_number, notes, updated_at)
            VALUES ($exerciseId, $equipmentKey, $seat, $angle, $machine, $notes, $now)
            ON CONFLICT(exercise_id, equipment_key) DO UPDATE SET seat_position=excluded.seat_position,
                bench_angle=excluded.bench_angle, machine_number=excluded.machine_number,
                notes=excluded.notes, updated_at=excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$exerciseId", preference.ExerciseId.ToString("D"));
        command.Parameters.AddWithValue("$equipmentKey", preference.EquipmentKey ?? string.Empty);
        command.Parameters.AddWithValue("$seat", preference.SeatPosition ?? string.Empty);
        command.Parameters.AddWithValue("$angle", preference.BenchAngle ?? string.Empty);
        command.Parameters.AddWithValue("$machine", preference.MachineNumber ?? string.Empty);
        command.Parameters.AddWithValue("$notes", preference.Notes ?? string.Empty);
        command.Parameters.AddWithValue("$now", now);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ExerciseSetupPreferenceData?> GetExerciseSetupPreferenceAsync(
        Guid exerciseId,
        string equipmentKey = "",
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT exercise_id, equipment_key, seat_position, bench_angle, machine_number, notes
            FROM exercise_setup_preferences WHERE exercise_id=$exerciseId AND equipment_key=$equipmentKey;
            """;
        command.Parameters.AddWithValue("$exerciseId", exerciseId.ToString("D"));
        command.Parameters.AddWithValue("$equipmentKey", equipmentKey ?? string.Empty);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new ExerciseSetupPreferenceData(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5))
            : null;
    }
}
