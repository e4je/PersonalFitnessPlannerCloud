using Microsoft.Data.Sqlite;

namespace PersonalFitnessPlanner.Infrastructure.Persistence;

public sealed class SqliteDatabase
{
    private const int BusyTimeoutMilliseconds = 5_000;
    private static readonly SemaphoreSlim MigrationGate = new(1, 1);
    private readonly AppPaths _paths;
    private readonly string _connectionString;

    public SqliteDatabase(AppPaths paths)
    {
        _paths = paths;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = paths.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            DefaultTimeout = 5
        }.ToString();
    }

    public string DatabasePath => _paths.DatabasePath;

    /// <summary>Opens a short-lived, fully configured connection.</summary>
    public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        _paths.EnsureCreated();
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA foreign_keys=ON; PRAGMA busy_timeout={BusyTimeoutMilliseconds};";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await MigrationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using (var wal = connection.CreateCommand())
            {
                wal.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
                await wal.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var bootstrap = connection.CreateCommand())
            {
                bootstrap.CommandText = """
                    CREATE TABLE IF NOT EXISTS schema_migrations (
                        version INTEGER NOT NULL PRIMARY KEY,
                        applied_at TEXT NOT NULL
                    );
                    """;
                await bootstrap.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            var currentVersion = await GetCurrentVersionAsync(connection, cancellationToken).ConfigureAwait(false);
            var latestVersion = Migrations.All.Max(x => x.Version);
            if (currentVersion > 0 && currentVersion < latestVersion)
            {
                await CreatePreMigrationBackupAsync(connection, currentVersion, cancellationToken).ConfigureAwait(false);
            }
            foreach (var migration in Migrations.All.Where(x => x.Version > currentVersion).OrderBy(x => x.Version))
            {
                await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await using var command = connection.CreateCommand();
                    command.Transaction = (SqliteTransaction)transaction;
                    command.CommandText = migration.Sql;
                    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                    command.Parameters.Clear();
                    command.CommandText = "INSERT INTO schema_migrations(version, applied_at) VALUES ($version, $appliedAt);";
                    command.Parameters.AddWithValue("$version", migration.Version);
                    command.Parameters.AddWithValue("$appliedAt", DateTimeOffset.UtcNow.ToString("O"));
                    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    throw;
                }
            }
        }
        finally
        {
            MigrationGate.Release();
        }
    }

    public async Task<int> GetSchemaVersionAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await GetCurrentVersionAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> GetCurrentVersionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_migrations;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }

    private async Task CreatePreMigrationBackupAsync(
        SqliteConnection source,
        int fromVersion,
        CancellationToken cancellationToken)
    {
        _paths.EnsureCreated();
        var backupPath = Path.Combine(
            _paths.BackupsDirectory,
            $"pre-migration-v{fromVersion}-{DateTime.Now:yyyyMMdd-HHmmss-fff}.db");
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = backupPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();
        await using var destination = new SqliteConnection(connectionString);
        await destination.OpenAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        source.BackupDatabase(destination);
        await destination.CloseAsync().ConfigureAwait(false);

        foreach (var oldBackup in new DirectoryInfo(_paths.BackupsDirectory)
                     .EnumerateFiles("*.db", SearchOption.TopDirectoryOnly)
                     .OrderByDescending(x => x.LastWriteTimeUtc)
                     .Skip(10))
        {
            oldBackup.Delete();
        }
    }

    private sealed record Migration(int Version, string Sql);

    private static class Migrations
    {
        public static readonly IReadOnlyList<Migration> All =
        [
            new(1, """
                CREATE TABLE plans (
                    id TEXT NOT NULL PRIMARY KEY,
                    plan_id TEXT NOT NULL,
                    name TEXT NOT NULL,
                    version INTEGER NOT NULL,
                    status TEXT NOT NULL CHECK(status IN ('draft','published','archived')),
                    intro_weeks INTEGER NOT NULL DEFAULT 2,
                    intro_max_sets INTEGER NOT NULL DEFAULT 2,
                    snapshot_json TEXT NOT NULL,
                    published_at TEXT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    deleted_at TEXT NULL,
                    UNIQUE(plan_id, version)
                );

                CREATE TABLE plan_assignments (
                    id TEXT NOT NULL PRIMARY KEY,
                    plan_version_id TEXT NOT NULL REFERENCES plans(id),
                    assigned_at TEXT NOT NULL,
                    is_active INTEGER NOT NULL DEFAULT 1 CHECK(is_active IN (0,1))
                );

                CREATE TABLE exercises (
                    id TEXT NOT NULL PRIMARY KEY,
                    name TEXT NOT NULL,
                    body_part TEXT NOT NULL,
                    equipment TEXT NOT NULL,
                    prescription TEXT NOT NULL,
                    cues TEXT NOT NULL,
                    common_mistakes TEXT NOT NULL,
                    alternatives TEXT NOT NULL,
                    version INTEGER NOT NULL DEFAULT 1,
                    status TEXT NOT NULL DEFAULT 'published',
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    deleted_at TEXT NULL
                );

                CREATE TABLE workout_sessions (
                    id TEXT NOT NULL PRIMARY KEY,
                    day_code TEXT NOT NULL,
                    local_date TEXT NOT NULL,
                    status TEXT NOT NULL CHECK(status IN ('active','completed','interrupted')),
                    source TEXT NOT NULL DEFAULT 'windows',
                    plan_version_id TEXT NOT NULL,
                    plan_snapshot_json TEXT NOT NULL,
                    started_at TEXT NOT NULL,
                    completed_at TEXT NULL,
                    ended_early INTEGER NOT NULL DEFAULT 0 CHECK(ended_early IN (0,1)),
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    deleted_at TEXT NULL
                );

                CREATE TABLE workout_sets (
                    id TEXT NOT NULL PRIMARY KEY,
                    session_id TEXT NOT NULL REFERENCES workout_sessions(id),
                    plan_item_id TEXT NOT NULL,
                    option_id TEXT NOT NULL,
                    exercise_name TEXT NOT NULL,
                    set_number INTEGER NOT NULL CHECK(set_number > 0),
                    weight_kg REAL NULL CHECK(weight_kg IS NULL OR weight_kg >= 0),
                    reps INTEGER NULL CHECK(reps IS NULL OR reps >= 0),
                    duration_seconds INTEGER NULL CHECK(duration_seconds IS NULL OR duration_seconds >= 0),
                    rir INTEGER NULL CHECK(rir IS NULL OR (rir >= 0 AND rir <= 10)),
                    pain INTEGER NOT NULL DEFAULT 0 CHECK(pain IN (0,1)),
                    notes TEXT NOT NULL DEFAULT '',
                    client_set_key TEXT NOT NULL UNIQUE,
                    completed_at TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    deleted_at TEXT NULL
                );

                CREATE TABLE outbox (
                    id TEXT NOT NULL PRIMARY KEY,
                    entity_type TEXT NOT NULL,
                    entity_id TEXT NOT NULL,
                    operation TEXT NOT NULL,
                    idempotency_key TEXT NOT NULL UNIQUE,
                    payload_json TEXT NOT NULL,
                    attempt_count INTEGER NOT NULL DEFAULT 0,
                    next_attempt_at TEXT NULL,
                    last_error TEXT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    processed_at TEXT NULL
                );

                CREATE TABLE sync_state (
                    stream TEXT NOT NULL PRIMARY KEY,
                    cursor TEXT NULL,
                    last_synced_at TEXT NULL
                );

                CREATE TABLE day_marks (
                    local_date TEXT NOT NULL PRIMARY KEY,
                    kind TEXT NOT NULL CHECK(kind IN ('rest','cardio')),
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );
                """),
            new(2, """
                CREATE INDEX ix_plans_status ON plans(status, deleted_at);
                CREATE INDEX ix_assignments_active ON plan_assignments(is_active, assigned_at DESC);
                CREATE INDEX ix_sessions_history ON workout_sessions(local_date DESC, deleted_at, status);
                CREATE INDEX ix_sets_session ON workout_sets(session_id, deleted_at, completed_at);
                CREATE INDEX ix_sets_exercise ON workout_sets(exercise_name, completed_at DESC);
                CREATE INDEX ix_outbox_pending ON outbox(processed_at, next_attempt_at, created_at);
                """),
            new(3, """
                CREATE TABLE sync_conflicts (
                    id TEXT NOT NULL PRIMARY KEY,
                    entity_type TEXT NOT NULL,
                    entity_id TEXT NOT NULL,
                    local_json TEXT NOT NULL,
                    server_json TEXT NOT NULL,
                    resolution TEXT NOT NULL DEFAULT 'server_wins',
                    created_at TEXT NOT NULL,
                    resolved_at TEXT NULL
                );
                CREATE INDEX ix_sync_conflicts_unresolved ON sync_conflicts(resolved_at, created_at);
                """),
            new(4, """
                ALTER TABLE workout_sessions ADD COLUMN effective_set_cap INTEGER NULL;
                """),
            new(5, """
                ALTER TABLE plan_assignments ADD COLUMN start_local_date TEXT NULL;
                UPDATE plan_assignments SET start_local_date=substr(assigned_at, 1, 10)
                    WHERE start_local_date IS NULL;

                ALTER TABLE outbox ADD COLUMN status TEXT NOT NULL DEFAULT 'pending'
                    CHECK(status IN ('pending','in_flight','failed','synced'));
                ALTER TABLE outbox ADD COLUMN in_flight_at TEXT NULL;
                ALTER TABLE outbox ADD COLUMN lock_token TEXT NULL;
                UPDATE outbox SET status=CASE WHEN processed_at IS NULL THEN 'pending' ELSE 'synced' END;

                CREATE TABLE daily_readiness (
                    id TEXT NOT NULL PRIMARY KEY,
                    local_date TEXT NOT NULL UNIQUE,
                    fatigue_score INTEGER NOT NULL CHECK(fatigue_score BETWEEN 0 AND 10),
                    sleep_quality INTEGER NULL CHECK(sleep_quality IS NULL OR sleep_quality BETWEEN 0 AND 10),
                    pain_notes TEXT NOT NULL DEFAULT '',
                    notes TEXT NOT NULL DEFAULT '',
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    deleted_at TEXT NULL
                );

                CREATE TABLE cardio_sessions (
                    id TEXT NOT NULL PRIMARY KEY,
                    local_date TEXT NOT NULL,
                    activity TEXT NOT NULL,
                    duration_minutes INTEGER NOT NULL CHECK(duration_minutes > 0),
                    distance_km REAL NULL CHECK(distance_km IS NULL OR distance_km >= 0),
                    started_at TEXT NOT NULL,
                    completed_at TEXT NULL,
                    notes TEXT NOT NULL DEFAULT '',
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    deleted_at TEXT NULL
                );

                CREATE TABLE exercise_setup_preferences (
                    exercise_id TEXT NOT NULL,
                    equipment_key TEXT NOT NULL DEFAULT '',
                    seat_position TEXT NOT NULL DEFAULT '',
                    bench_angle TEXT NOT NULL DEFAULT '',
                    machine_number TEXT NOT NULL DEFAULT '',
                    notes TEXT NOT NULL DEFAULT '',
                    updated_at TEXT NOT NULL,
                    PRIMARY KEY(exercise_id, equipment_key)
                );

                CREATE INDEX ix_readiness_date ON daily_readiness(local_date DESC, deleted_at);
                CREATE INDEX ix_cardio_date ON cardio_sessions(local_date DESC, deleted_at);
                CREATE INDEX ix_outbox_status ON outbox(status, next_attempt_at, created_at);
                """),
            new(6, """
                ALTER TABLE exercises ADD COLUMN equipment_id TEXT NULL;

                CREATE TABLE equipment_cache (
                    id TEXT NOT NULL PRIMARY KEY,
                    name TEXT NOT NULL,
                    category TEXT NOT NULL DEFAULT '',
                    payload_json TEXT NOT NULL,
                    entity_version INTEGER NOT NULL DEFAULT 1,
                    updated_at TEXT NOT NULL,
                    deleted_at TEXT NULL
                );
                CREATE INDEX ix_equipment_cache_active ON equipment_cache(deleted_at, name);
                """),
            new(7, """
                CREATE TABLE IF NOT EXISTS workout_sets (
                    id TEXT NOT NULL PRIMARY KEY,
                    session_id TEXT NOT NULL REFERENCES workout_sessions(id),
                    plan_item_id TEXT NOT NULL,
                    option_id TEXT NOT NULL,
                    exercise_name TEXT NOT NULL,
                    set_number INTEGER NOT NULL CHECK(set_number > 0),
                    weight_kg REAL NULL CHECK(weight_kg IS NULL OR weight_kg >= 0),
                    reps INTEGER NULL CHECK(reps IS NULL OR reps >= 0),
                    duration_seconds INTEGER NULL CHECK(duration_seconds IS NULL OR duration_seconds >= 0),
                    rir INTEGER NULL CHECK(rir IS NULL OR (rir >= 0 AND rir <= 10)),
                    pain INTEGER NOT NULL DEFAULT 0 CHECK(pain IN (0,1)),
                    notes TEXT NOT NULL DEFAULT '',
                    client_set_key TEXT NOT NULL UNIQUE,
                    completed_at TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    deleted_at TEXT NULL
                );
                ALTER TABLE workout_sets ADD COLUMN exercise_id TEXT NULL;
                ALTER TABLE workout_sets ADD COLUMN equipment_key TEXT NOT NULL DEFAULT '';
                ALTER TABLE workout_sets ADD COLUMN is_warmup INTEGER NOT NULL DEFAULT 0 CHECK(is_warmup IN (0,1));
                CREATE INDEX ix_sets_exact_history ON workout_sets(
                    exercise_id, option_id, equipment_key, completed_at DESC, deleted_at);
                """),
            new(8, """
                ALTER TABLE plans ADD COLUMN entity_version INTEGER NOT NULL DEFAULT 0;

                ALTER TABLE workout_sessions ADD COLUMN plan_assignment_id TEXT NULL;
                ALTER TABLE workout_sessions ADD COLUMN plan_day_id TEXT NULL;
                ALTER TABLE workout_sessions ADD COLUMN timezone TEXT NOT NULL DEFAULT 'UTC';
                ALTER TABLE workout_sessions ADD COLUMN entity_version INTEGER NOT NULL DEFAULT 0;

                ALTER TABLE workout_sets ADD COLUMN equipment_id TEXT NULL;
                ALTER TABLE workout_sets ADD COLUMN entity_version INTEGER NOT NULL DEFAULT 0;

                ALTER TABLE daily_readiness ADD COLUMN entity_version INTEGER NOT NULL DEFAULT 0;

                ALTER TABLE cardio_sessions ADD COLUMN source TEXT NOT NULL DEFAULT 'windows';
                ALTER TABLE cardio_sessions ADD COLUMN entity_version INTEGER NOT NULL DEFAULT 0;

                CREATE TABLE user_cache (
                    id TEXT NOT NULL PRIMARY KEY,
                    email TEXT NOT NULL DEFAULT '',
                    display_name TEXT NOT NULL DEFAULT '',
                    timezone TEXT NOT NULL DEFAULT 'UTC',
                    weight_unit TEXT NOT NULL DEFAULT 'KG',
                    payload_json TEXT NOT NULL,
                    entity_version INTEGER NOT NULL DEFAULT 0,
                    updated_at TEXT NOT NULL,
                    deleted_at TEXT NULL
                );
                """)
        ];
    }
}
