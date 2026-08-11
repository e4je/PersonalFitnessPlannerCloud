using Microsoft.Data.Sqlite;
using PersonalFitnessPlanner.Infrastructure.Models;

namespace PersonalFitnessPlanner.Infrastructure.Persistence;

public sealed partial class FitnessRepository
{
    public async Task<PlanData> GetCurrentPlanAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.snapshot_json
            FROM plans p
            LEFT JOIN plan_assignments a ON a.plan_version_id = p.id AND a.is_active = 1
            WHERE p.deleted_at IS NULL AND p.status = 'published'
            ORDER BY CASE WHEN a.id IS NULL THEN 1 ELSE 0 END, a.assigned_at DESC, p.version DESC
            LIMIT 1;
            """;
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is string json
            ? DeserializePlan(json)
            : throw new InvalidOperationException("没有可用的已发布训练计划。");
    }

    public async Task<IReadOnlyList<PlanData>> GetPlanVersionsAsync(CancellationToken cancellationToken = default)
    {
        var plans = new List<PlanData>();
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT snapshot_json FROM plans WHERE deleted_at IS NULL ORDER BY version DESC, updated_at DESC;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            plans.Add(DeserializePlan(reader.GetString(0)));
        }

        return plans;
    }

    public async Task<PlanData> CreatePlanDraftAsync(CancellationToken cancellationToken = default)
    {
        var current = await GetCurrentPlanAsync(cancellationToken).ConfigureAwait(false);
        var versions = await GetPlanVersionsAsync(cancellationToken).ConfigureAwait(false);
        var nextVersion = versions.Where(x => x.PlanId == current.PlanId).Select(x => x.Version).DefaultIfEmpty().Max() + 1;
        var draft = current with
        {
            Id = Guid.NewGuid(),
            Version = nextVersion,
            Status = "draft",
            PublishedAt = null
        };

        ValidatePlan(draft);
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await InsertPlanAsync(connection, (SqliteTransaction)transaction, draft, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return draft;
    }

    public async Task SavePlanDraftAsync(PlanData plan, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(plan.Status, "draft", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("已发布版本不可修改；请创建新草稿版本。");
        }

        ValidatePlan(plan);
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                UPDATE plans SET name=$name, intro_weeks=$introWeeks, intro_max_sets=$introMaxSets,
                    snapshot_json=$snapshot, updated_at=$updatedAt
                WHERE id=$id AND status='draft' AND deleted_at IS NULL;
                """;
            command.Parameters.AddWithValue("$name", plan.Name);
            command.Parameters.AddWithValue("$introWeeks", plan.DeloadWeeks);
            command.Parameters.AddWithValue("$introMaxSets", plan.DeloadMaxSets);
            command.Parameters.AddWithValue("$snapshot", Serialize(plan));
            command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$id", plan.Id.ToString("D"));
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException("草稿不存在，或该版本已经发布而不可修改。");
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public Task<PlanData> PublishPlanAsync(PlanData draft, CancellationToken cancellationToken = default) =>
        PublishPlanAsync(draft, enqueueOutbox: true, cancellationToken);

    public async Task<PlanData> PublishPlanAsync(
        PlanData draft,
        bool enqueueOutbox,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(draft.Status, "draft", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("只能发布草稿版本。");
        }

        ValidatePlan(draft);
        var published = draft with { Status = "published", PublishedAt = DateTimeOffset.UtcNow };
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                UPDATE plans SET status='published', name=$name, intro_weeks=$introWeeks,
                    intro_max_sets=$introMaxSets, snapshot_json=$snapshot, published_at=$publishedAt,
                    updated_at=$updatedAt
                WHERE id=$id AND status='draft' AND deleted_at IS NULL;
                """;
            command.Parameters.AddWithValue("$name", published.Name);
            command.Parameters.AddWithValue("$introWeeks", published.DeloadWeeks);
            command.Parameters.AddWithValue("$introMaxSets", published.DeloadMaxSets);
            command.Parameters.AddWithValue("$snapshot", Serialize(published));
            command.Parameters.AddWithValue("$publishedAt", published.PublishedAt!.Value.ToString("O"));
            command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$id", published.Id.ToString("D"));
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException("草稿不存在，或已被其他操作发布。");
            }

            if (enqueueOutbox)
            {
                await EnqueueOutboxAsync(connection, (SqliteTransaction)transaction, "plan_version", published.Id,
                    "publish", $"plan:{published.Id:D}:publish", published, cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return published;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public Task AssignPlanAsync(Guid planVersionId, CancellationToken cancellationToken = default) =>
        AssignPlanAsync(planVersionId, Guid.NewGuid(), enqueueOutbox: true, cancellationToken);

    public async Task AssignPlanAsync(
        Guid planVersionId,
        Guid assignmentId,
        bool enqueueOutbox,
        CancellationToken cancellationToken = default)
    {
        if (assignmentId == Guid.Empty) throw new ArgumentException("分配 UUID 不能为空。", nameof(assignmentId));
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using (var exists = connection.CreateCommand())
            {
                exists.Transaction = (SqliteTransaction)transaction;
                exists.CommandText = "SELECT COUNT(*) FROM plans WHERE id=$id AND status='published' AND deleted_at IS NULL;";
                exists.Parameters.AddWithValue("$id", planVersionId.ToString("D"));
                if (Convert.ToInt32(await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) != 1)
                {
                    throw new InvalidOperationException("只能分配存在的已发布计划版本。");
                }
            }

            await using (var deactivate = connection.CreateCommand())
            {
                deactivate.Transaction = (SqliteTransaction)transaction;
                deactivate.CommandText = "UPDATE plan_assignments SET is_active=0 WHERE is_active=1;";
                await deactivate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            var now = DateTimeOffset.UtcNow;
            await using (var assign = connection.CreateCommand())
            {
                assign.Transaction = (SqliteTransaction)transaction;
                assign.CommandText = """
                    INSERT INTO plan_assignments(id, plan_version_id, assigned_at, is_active, start_local_date)
                    VALUES ($id, $planVersionId, $assignedAt, 1, $startLocalDate);
                    """;
                assign.Parameters.AddWithValue("$id", assignmentId.ToString("D"));
                assign.Parameters.AddWithValue("$planVersionId", planVersionId.ToString("D"));
                assign.Parameters.AddWithValue("$assignedAt", now.ToString("O"));
                assign.Parameters.AddWithValue("$startLocalDate", DateOnly.FromDateTime(DateTime.Today).ToString("yyyy-MM-dd"));
                await assign.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            if (enqueueOutbox)
            {
                await EnqueueOutboxAsync(connection, (SqliteTransaction)transaction, "plan_assignment", assignmentId,
                    "upsert", $"assignment:{assignmentId:D}", new { id = assignmentId, planVersionId, assignedAt = now }, cancellationToken)
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

    public Task RollbackAssignmentAsync(Guid planVersionId, CancellationToken cancellationToken = default) =>
        AssignPlanAsync(planVersionId, cancellationToken);

    private static async Task InsertPlanAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PlanData plan,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO plans(id, plan_id, name, version, status, intro_weeks, intro_max_sets,
                snapshot_json, published_at, created_at, updated_at, deleted_at)
            VALUES ($id, $planId, $name, $version, $status, $introWeeks, $introMaxSets,
                $snapshot, $publishedAt, $now, $now, NULL);
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
        command.Parameters.AddWithValue("$now", now);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
