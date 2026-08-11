using Microsoft.Data.Sqlite;

namespace PersonalFitnessPlanner.Infrastructure.Persistence;

public sealed record AccountScopePreparation(
    bool Changed,
    string? PreviousSubject);

public sealed class AccountSwitchBlockedException : InvalidOperationException
{
    public AccountSwitchBlockedException(int pendingOutboxCount, int localDraftCount)
        : base(BuildMessage(pendingOutboxCount, localDraftCount))
    {
        PendingOutboxCount = pendingOutboxCount;
        LocalDraftCount = localDraftCount;
    }

    public int PendingOutboxCount { get; }

    public int LocalDraftCount { get; }

    private static string BuildMessage(int pendingOutboxCount, int localDraftCount)
    {
        var reasons = new List<string>();
        if (pendingOutboxCount > 0) reasons.Add($"{pendingOutboxCount} 条待上传记录");
        if (localDraftCount > 0) reasons.Add($"{localDraftCount} 个本地草稿");
        return $"检测到账号切换，但当前账号仍有{string.Join("和", reasons)}。为避免把原账号数据上传到新账号，已取消登录；请重新登录原账号完成同步或处理本地草稿后再切换。";
    }
}

public sealed partial class FitnessRepository
{
    internal const string AccountSubjectStream = "account_subject";

    /// <summary>
    /// Binds the single-profile SQLite cache to one authenticated server subject.
    /// The legacy desktop schema is not user-partitioned, so changing subjects must
    /// clear the previous server mirror and cursor before any request can use the
    /// new token. Pending writes and local drafts fail closed instead of being
    /// silently reassigned to the new account.
    /// </summary>
    public async Task<AccountScopePreparation> PrepareAccountScopeAsync(
        string authenticatedSubject,
        CancellationToken cancellationToken = default)
    {
        var subject = authenticatedSubject.Trim();
        if (string.IsNullOrWhiteSpace(subject))
            throw new ArgumentException("登录令牌缺少用户标识，不能安全绑定本地缓存。", nameof(authenticatedSubject));

        var defaultPlan = await _defaultPlanLoader.LoadAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var previousSubject = await ReadAccountSubjectAsync(
                connection, (SqliteTransaction)transaction, cancellationToken).ConfigureAwait(false);
            if (string.Equals(previousSubject, subject, StringComparison.OrdinalIgnoreCase))
            {
                await WriteAccountSubjectAsync(
                    connection, (SqliteTransaction)transaction, subject, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new AccountScopePreparation(false, previousSubject);
            }

            var hasScopedState = previousSubject is not null || await HasUnboundAccountStateAsync(
                connection, (SqliteTransaction)transaction, cancellationToken).ConfigureAwait(false);
            if (!hasScopedState)
            {
                await WriteAccountSubjectAsync(
                    connection, (SqliteTransaction)transaction, subject, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new AccountScopePreparation(false, null);
            }

            var pendingOutboxCount = await ScalarIntAsync(
                connection,
                (SqliteTransaction)transaction,
                "SELECT COUNT(*) FROM outbox WHERE processed_at IS NULL;",
                cancellationToken).ConfigureAwait(false);
            var localDraftCount = await ScalarIntAsync(
                connection,
                (SqliteTransaction)transaction,
                """
                SELECT
                  (SELECT COUNT(*) FROM plans WHERE status='draft' AND deleted_at IS NULL) +
                  (SELECT COUNT(*) FROM exercises WHERE status='draft' AND deleted_at IS NULL);
                """,
                cancellationToken).ConfigureAwait(false);
            if (pendingOutboxCount > 0 || localDraftCount > 0)
                throw new AccountSwitchBlockedException(pendingOutboxCount, localDraftCount);

            var now = DateTimeOffset.UtcNow.ToString("O");
            var defaultExerciseIds = defaultPlan.Days
                .SelectMany(day => day.Items)
                .SelectMany(item => item.Options)
                .Select(option => option.ExerciseId)
                .Distinct()
                .ToArray();
            var exerciseParameters = defaultExerciseIds
                .Select((_, index) => $"$exercise{index}")
                .ToArray();

            await using (var clear = connection.CreateCommand())
            {
                clear.Transaction = (SqliteTransaction)transaction;
                clear.CommandText = $"""
                    DELETE FROM workout_sets;
                    DELETE FROM workout_sessions;
                    DELETE FROM daily_readiness;
                    DELETE FROM cardio_sessions;
                    DELETE FROM day_marks;
                    DELETE FROM exercise_setup_preferences;
                    DELETE FROM sync_conflicts;
                    DELETE FROM outbox;
                    DELETE FROM plan_assignments;

                    UPDATE user_cache SET deleted_at=$now, updated_at=$now
                    WHERE deleted_at IS NULL;
                    UPDATE plans SET deleted_at=$now, updated_at=$now
                    WHERE status <> 'draft' AND id <> $defaultPlanId AND deleted_at IS NULL;
                    UPDATE exercises SET deleted_at=$now, updated_at=$now
                    WHERE status <> 'draft'
                      AND id NOT IN ({string.Join(",", exerciseParameters)})
                      AND deleted_at IS NULL;
                    UPDATE equipment_cache SET deleted_at=$now WHERE deleted_at IS NULL;
                    DELETE FROM sync_state;
                    """;
                clear.Parameters.AddWithValue("$now", now);
                clear.Parameters.AddWithValue("$defaultPlanId", defaultPlan.Id.ToString("D"));
                for (var index = 0; index < defaultExerciseIds.Length; index++)
                    clear.Parameters.AddWithValue(exerciseParameters[index], defaultExerciseIds[index].ToString("D"));
                await clear.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await EnsureDefaultFallbackAsync(
                connection,
                (SqliteTransaction)transaction,
                defaultPlan,
                subject,
                cancellationToken).ConfigureAwait(false);
            await WriteAccountSubjectAsync(
                connection, (SqliteTransaction)transaction, subject, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new AccountScopePreparation(true, previousSubject);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<string?> ReadAccountSubjectAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT cursor FROM sync_state WHERE stream=$stream LIMIT 1;";
            command.Parameters.AddWithValue("$stream", AccountSubjectStream);
            if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string stored &&
                !string.IsNullOrWhiteSpace(stored))
            {
                return stored.Trim();
            }
        }

        await using var inferred = connection.CreateCommand();
        inferred.Transaction = transaction;
        inferred.CommandText = "SELECT id FROM user_cache WHERE deleted_at IS NULL ORDER BY updated_at DESC LIMIT 2;";
        await using var reader = await inferred.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var subjects = new List<string>(2);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            subjects.Add(reader.GetString(0));
        return subjects.Count == 1 ? subjects[0] : null;
    }

    private static async Task<bool> HasUnboundAccountStateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS(
              SELECT 1 FROM sync_state
                WHERE stream <> $accountStream AND COALESCE(cursor, '') <> ''
              UNION ALL SELECT 1 FROM user_cache WHERE deleted_at IS NULL
              UNION ALL SELECT 1 FROM workout_sessions WHERE deleted_at IS NULL
              UNION ALL SELECT 1 FROM daily_readiness WHERE deleted_at IS NULL
              UNION ALL SELECT 1 FROM cardio_sessions WHERE deleted_at IS NULL
              UNION ALL SELECT 1 FROM day_marks
              UNION ALL SELECT 1 FROM exercise_setup_preferences
              UNION ALL SELECT 1 FROM outbox WHERE processed_at IS NULL
              UNION ALL SELECT 1 FROM plans WHERE status='draft' AND deleted_at IS NULL
              UNION ALL SELECT 1 FROM exercises WHERE status='draft' AND deleted_at IS NULL
            );
            """;
        command.Parameters.AddWithValue("$accountStream", AccountSubjectStream);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) != 0;
    }

    private static async Task WriteAccountSubjectAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string subject,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO sync_state(stream, cursor, last_synced_at)
            VALUES ($stream, $subject, $now)
            ON CONFLICT(stream) DO UPDATE SET cursor=excluded.cursor, last_synced_at=excluded.last_synced_at;
            """;
        command.Parameters.AddWithValue("$stream", AccountSubjectStream);
        command.Parameters.AddWithValue("$subject", subject);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> ScalarIntAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }

    private static async Task EnsureDefaultFallbackAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Models.PlanData defaultPlan,
        string subject,
        CancellationToken cancellationToken)
    {
        await using (var exists = connection.CreateCommand())
        {
            exists.Transaction = transaction;
            exists.CommandText = "SELECT COUNT(*) FROM plans WHERE id=$id;";
            exists.Parameters.AddWithValue("$id", defaultPlan.Id.ToString("D"));
            if (Convert.ToInt32(await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) == 0)
                await InsertPlanAsync(connection, transaction, defaultPlan, cancellationToken).ConfigureAwait(false);
        }

        await using (var restore = connection.CreateCommand())
        {
            restore.Transaction = transaction;
            restore.CommandText = "UPDATE plans SET deleted_at=NULL WHERE id=$id;";
            restore.Parameters.AddWithValue("$id", defaultPlan.Id.ToString("D"));
            await restore.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var assignmentId = CreateStableLocalId($"account-fallback:{subject}:{defaultPlan.Id:D}");
        await using var assignment = connection.CreateCommand();
        assignment.Transaction = transaction;
        assignment.CommandText = """
            INSERT INTO plan_assignments(id, plan_version_id, assigned_at, is_active, start_local_date)
            VALUES ($id, $planVersionId, $now, 1, $startDate);
            """;
        assignment.Parameters.AddWithValue("$id", assignmentId.ToString("D"));
        assignment.Parameters.AddWithValue("$planVersionId", defaultPlan.Id.ToString("D"));
        assignment.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        assignment.Parameters.AddWithValue("$startDate", DateOnly.FromDateTime(DateTime.Today).ToString("yyyy-MM-dd"));
        await assignment.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
