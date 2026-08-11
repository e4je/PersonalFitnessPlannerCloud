using System.Text.Json;
using Microsoft.Data.Sqlite;
using PersonalFitnessPlanner.Infrastructure.Data;
using PersonalFitnessPlanner.Infrastructure.Models;

namespace PersonalFitnessPlanner.Infrastructure.Persistence;

/// <summary>
/// SQLite repository. Every public operation opens and disposes its own
/// connection; multi-row state changes use an explicit transaction.
/// </summary>
public sealed partial class FitnessRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    private readonly SqliteDatabase _database;
    private readonly DefaultPlanLoader _defaultPlanLoader;

    public FitnessRepository(SqliteDatabase database, DefaultPlanLoader? defaultPlanLoader = null)
    {
        _database = database;
        _defaultPlanLoader = defaultPlanLoader ?? new DefaultPlanLoader();
    }

    public SqliteDatabase Database => _database;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM plans WHERE deleted_at IS NULL;";
        if (Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) > 0)
        {
            return;
        }

        var plan = await _defaultPlanLoader.LoadAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await InsertPlanAsync(connection, (SqliteTransaction)transaction, plan, cancellationToken).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            await using (var assignment = connection.CreateCommand())
            {
                assignment.Transaction = (SqliteTransaction)transaction;
                assignment.CommandText = """
                    INSERT INTO plan_assignments(id, plan_version_id, assigned_at, is_active, start_local_date)
                    VALUES ($id, $planVersionId, $assignedAt, 1, $startLocalDate);
                    """;
                assignment.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
                assignment.Parameters.AddWithValue("$planVersionId", plan.Id.ToString("D"));
                assignment.Parameters.AddWithValue("$assignedAt", now.ToString("O"));
                assignment.Parameters.AddWithValue("$startLocalDate", DateOnly.FromDateTime(DateTime.Today).ToString("yyyy-MM-dd"));
                await assignment.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            foreach (var item in plan.Days.SelectMany(x => x.Items))
            {
                var alternatives = string.Join("、", item.Options.Where(x => !x.IsPreferred).Select(x => x.ExerciseName));
                foreach (var option in item.Options)
                {
                    await using var exercise = connection.CreateCommand();
                    exercise.Transaction = (SqliteTransaction)transaction;
                    exercise.CommandText = """
                        INSERT OR IGNORE INTO exercises(
                            id, name, body_part, equipment, prescription, cues, common_mistakes,
                            alternatives, version, status, created_at, updated_at, deleted_at)
                        VALUES ($id, $name, $bodyPart, $equipment, $prescription, $cues,
                            $commonMistakes, $alternatives, 1, 'published', $now, $now, NULL);
                        """;
                    exercise.Parameters.AddWithValue("$id", option.ExerciseId.ToString("D"));
                    exercise.Parameters.AddWithValue("$name", option.ExerciseName);
                    exercise.Parameters.AddWithValue("$bodyPart", item.BodyPart);
                    exercise.Parameters.AddWithValue("$equipment", option.Equipment);
                    exercise.Parameters.AddWithValue("$prescription", FormatPrescription(option));
                    exercise.Parameters.AddWithValue("$cues", item.Cues);
                    exercise.Parameters.AddWithValue("$commonMistakes", item.CommonMistakes);
                    exercise.Parameters.AddWithValue("$alternatives", alternatives);
                    exercise.Parameters.AddWithValue("$now", now.ToString("O"));
                    await exercise.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

    private static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, JsonOptions)
        ?? throw new InvalidDataException($"无法读取本地 {typeof(T).Name} JSON。");

    private static PlanData DeserializePlan(string json)
    {
        var plan = Deserialize<PlanData>(json);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("训练计划快照必须是 JSON 对象。");
        }

        var root = document.RootElement;
        return plan with
        {
            WeeklyStrengthTarget = ReadPlanRule(
                root, 3, 1, 7, "weeklyStrengthTarget", "weekly_strength_target", "weekly_frequency"),
            MinimumRestDays = ReadPlanRule(
                root, 1, 0, 14, "minimumRestDays", "minimum_rest_days", "min_rest_days"),
            FatigueThreshold = ReadPlanRule(
                root, 8, 1, 10, "fatigueThreshold", "fatigue_threshold")
        };
    }

    private static int ReadPlanRule(
        JsonElement root,
        int legacyDefault,
        int minimum,
        int maximum,
        params string[] names)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (!names.Contains(property.Name, StringComparer.OrdinalIgnoreCase)) continue;
            if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out var value) ||
                value < minimum || value > maximum)
            {
                throw new InvalidDataException($"训练计划规则 {property.Name} 必须在 {minimum}～{maximum} 范围内。");
            }

            return value;
        }

        return legacyDefault;
    }

    private static string FormatPrescription(ExerciseOptionData option)
    {
        var unit = option.RepUnit switch
        {
            "seconds" => "秒",
            "reps_per_side" => "次/侧",
            _ => "次"
        };
        return $"{option.Sets}×{option.RepMin}～{option.RepMax}{unit}";
    }

    private static void ValidatePlan(PlanData plan)
    {
        if (string.IsNullOrWhiteSpace(plan.Name) || plan.Days.Count == 0)
        {
            throw new ArgumentException("计划名称和训练日不能为空。", nameof(plan));
        }

        if (plan.WeeklyStrengthTarget is < 1 or > 7 ||
            plan.MinimumRestDays is < 0 or > 14 ||
            plan.FatigueThreshold is < 1 or > 10)
        {
            throw new ArgumentException("计划的周训练目标、最少休息日或疲劳阈值无效。", nameof(plan));
        }

        foreach (var day in plan.Days)
        {
            if (string.IsNullOrWhiteSpace(day.Code) || day.Items.GroupBy(x => x.Position).Any(x => x.Count() > 1))
            {
                throw new ArgumentException($"训练日 {day.Code} 存在重复位置。", nameof(plan));
            }

            if (day.Items.Any(x => x.Position <= 0 || x.Options.Count == 0 ||
                                   x.Options.Count(o => o.IsPreferred) != 1 ||
                                   x.Options.Any(o => o.Sets <= 0 || o.RepMin <= 0 || o.RepMax < o.RepMin)))
            {
                throw new ArgumentException($"训练日 {day.Code} 存在无效位置或组次。", nameof(plan));
            }
        }
    }

    private static async Task EnqueueOutboxAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string entityType,
        Guid entityId,
        string operation,
        string idempotencyKey,
        object payload,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO outbox(
                id, entity_type, entity_id, operation, idempotency_key, payload_json,
                attempt_count, next_attempt_at, last_error, created_at, updated_at, processed_at)
            VALUES ($id, $entityType, $entityId, $operation, $idempotencyKey, $payload,
                0, NULL, NULL, $now, $now, NULL);
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$entityType", entityType);
        command.Parameters.AddWithValue("$entityId", entityId.ToString("D"));
        command.Parameters.AddWithValue("$operation", operation);
        command.Parameters.AddWithValue("$idempotencyKey", idempotencyKey);
        command.Parameters.AddWithValue("$payload", Serialize(payload));
        command.Parameters.AddWithValue("$now", now);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
