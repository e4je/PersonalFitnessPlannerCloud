using Microsoft.Data.Sqlite;
using PersonalFitnessPlanner.Infrastructure.Models;

namespace PersonalFitnessPlanner.Infrastructure.Persistence;

public sealed partial class FitnessRepository
{
    public async Task<IReadOnlyList<ExerciseLibraryItem>> GetExercisesAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<ExerciseLibraryItem>();
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, body_part, equipment, prescription, cues, common_mistakes,
                alternatives, version, status
            FROM exercises WHERE deleted_at IS NULL
            ORDER BY body_part, name;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new ExerciseLibraryItem(
                Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6),
                reader.GetString(7), reader.GetInt64(8), reader.GetString(9)));
        }
        return result;
    }

    public async Task SaveExerciseDraftAsync(ExerciseLibraryItem exercise, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(exercise.Name) || string.IsNullOrWhiteSpace(exercise.BodyPart))
        {
            throw new ArgumentException("动作名称和部位不能为空。", nameof(exercise));
        }

        var now = DateTimeOffset.UtcNow.ToString("O");
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO exercises(id, name, body_part, equipment, prescription, cues, common_mistakes,
                alternatives, version, status, created_at, updated_at, deleted_at)
            VALUES ($id, $name, $bodyPart, $equipment, $prescription, $cues, $mistakes,
                $alternatives, $version, 'draft', $now, $now, NULL)
            ON CONFLICT(id) DO UPDATE SET
                name=excluded.name, body_part=excluded.body_part, equipment=excluded.equipment,
                prescription=excluded.prescription, cues=excluded.cues,
                common_mistakes=excluded.common_mistakes, alternatives=excluded.alternatives,
                version=excluded.version, updated_at=excluded.updated_at
            WHERE exercises.status='draft' AND exercises.deleted_at IS NULL;
            """;
        command.Parameters.AddWithValue("$id", exercise.Id.ToString("D"));
        command.Parameters.AddWithValue("$name", exercise.Name.Trim());
        command.Parameters.AddWithValue("$bodyPart", exercise.BodyPart.Trim());
        command.Parameters.AddWithValue("$equipment", exercise.Equipment ?? string.Empty);
        command.Parameters.AddWithValue("$prescription", exercise.Prescription ?? string.Empty);
        command.Parameters.AddWithValue("$cues", exercise.Cues ?? string.Empty);
        command.Parameters.AddWithValue("$mistakes", exercise.CommonMistakes ?? string.Empty);
        command.Parameters.AddWithValue("$alternatives", exercise.Alternatives ?? string.Empty);
        command.Parameters.AddWithValue("$version", Math.Max(1, exercise.Version));
        command.Parameters.AddWithValue("$now", now);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("已发布动作不可原地修改，请创建新的动作草稿。");
        }
    }

    public Task PublishExerciseAsync(Guid exerciseId, CancellationToken cancellationToken = default) =>
        PublishExerciseAsync(exerciseId, enqueueOutbox: true, cancellationToken);

    public async Task PublishExerciseAsync(
        Guid exerciseId,
        bool enqueueOutbox,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = DateTimeOffset.UtcNow;
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                UPDATE exercises SET status='published', updated_at=$now
                WHERE id=$id AND status='draft' AND deleted_at IS NULL;
                """;
            command.Parameters.AddWithValue("$now", now.ToString("O"));
            command.Parameters.AddWithValue("$id", exerciseId.ToString("D"));
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException("动作草稿不存在或已经发布。");
            }

            if (enqueueOutbox)
            {
                await EnqueueOutboxAsync(connection, (SqliteTransaction)transaction, "exercise", exerciseId,
                    "publish", $"exercise:{exerciseId:D}:publish", new { id = exerciseId, publishedAt = now }, cancellationToken)
                    .ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }
}
