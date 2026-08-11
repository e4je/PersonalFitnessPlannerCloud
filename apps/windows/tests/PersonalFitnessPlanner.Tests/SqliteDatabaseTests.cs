using PersonalFitnessPlanner.Infrastructure;
using PersonalFitnessPlanner.Infrastructure.Persistence;

namespace PersonalFitnessPlanner.Tests;

public sealed class SqliteDatabaseTests
{
    private const int LatestSchemaVersion = 8;

    [Fact]
    public async Task InitializeAsync_MigratesIdempotently_InChinesePathWithSpaces()
    {
        using var temporary = new TemporaryDirectory("健身 数据库 migration");
        var paths = new AppPaths(temporary.Path);
        var database = new SqliteDatabase(paths);

        await database.InitializeAsync();
        var firstVersion = await database.GetSchemaVersionAsync();
        await database.InitializeAsync();
        var secondVersion = await database.GetSchemaVersionAsync();

        Assert.Equal(LatestSchemaVersion, firstVersion);
        Assert.Equal(firstVersion, secondVersion);
        Assert.True(File.Exists(paths.DatabasePath));
        Assert.True(Directory.Exists(paths.LogsDirectory));
        Assert.True(Directory.Exists(paths.CacheDirectory));
        Assert.True(Directory.Exists(paths.BackupsDirectory));

        await using var connection = await database.OpenConnectionAsync();
        await using var tableCommand = connection.CreateCommand();
        tableCommand.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table';";
        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var reader = await tableCommand.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                tables.Add(reader.GetString(0));
            }
        }

        Assert.Contains("schema_migrations", tables);
        Assert.Contains("plans", tables);
        Assert.Contains("workout_sessions", tables);
        Assert.Contains("workout_sets", tables);
        Assert.Contains("outbox", tables);
        Assert.Contains("sync_state", tables);
        Assert.Contains("user_cache", tables);

        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys;";
        Assert.Equal(1L, (long)(await pragma.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task InitializeAsync_UpgradesLegacyV3Database_WithoutLosingExistingRows()
    {
        using var temporary = new TemporaryDirectory("旧版 数据迁移");
        var paths = new AppPaths(temporary.Path);
        paths.EnsureCreated();
        await using (var legacy = new Microsoft.Data.Sqlite.SqliteConnection(
                         $"Data Source={paths.DatabasePath};Pooling=False"))
        {
            await legacy.OpenAsync();
            await using var create = legacy.CreateCommand();
            create.CommandText = """
                CREATE TABLE schema_migrations(version INTEGER NOT NULL PRIMARY KEY, applied_at TEXT NOT NULL);
                INSERT INTO schema_migrations(version, applied_at) VALUES
                    (1, '2026-01-01T00:00:00Z'),
                    (2, '2026-01-02T00:00:00Z'),
                    (3, '2026-01-03T00:00:00Z');

                CREATE TABLE plans(id TEXT NOT NULL PRIMARY KEY);
                INSERT INTO plans(id) VALUES ('legacy-plan');

                CREATE TABLE workout_sessions(id TEXT NOT NULL PRIMARY KEY);
                INSERT INTO workout_sessions(id) VALUES ('legacy-session');

                CREATE TABLE workout_sets(
                    id TEXT NOT NULL PRIMARY KEY,
                    option_id TEXT NOT NULL,
                    completed_at TEXT NOT NULL,
                    deleted_at TEXT NULL);
                INSERT INTO workout_sets(id, option_id, completed_at, deleted_at)
                    VALUES ('legacy-set', 'legacy-option', '2026-07-01T08:32:00+00:00', NULL);

                CREATE TABLE plan_assignments(id TEXT NOT NULL PRIMARY KEY, assigned_at TEXT NOT NULL);
                INSERT INTO plan_assignments(id, assigned_at)
                    VALUES ('legacy-assignment', '2026-07-01T08:30:00+00:00');

                CREATE TABLE outbox(
                    id TEXT NOT NULL PRIMARY KEY,
                    processed_at TEXT NULL,
                    next_attempt_at TEXT NULL,
                    created_at TEXT NOT NULL);
                INSERT INTO outbox(id, processed_at, next_attempt_at, created_at)
                    VALUES ('legacy-outbox', NULL, NULL, '2026-07-01T08:31:00+00:00');

                CREATE TABLE exercises(id TEXT NOT NULL PRIMARY KEY);
                INSERT INTO exercises(id) VALUES ('legacy-exercise');
                """;
            await create.ExecuteNonQueryAsync();
        }

        var database = new SqliteDatabase(paths);
        await database.InitializeAsync();

        Assert.Equal(LatestSchemaVersion, await database.GetSchemaVersionAsync());
        await using var connection = await database.OpenConnectionAsync();
        await using var verify = connection.CreateCommand();
        verify.CommandText = """
            SELECT
              (SELECT COUNT(*) FROM workout_sessions WHERE id='legacy-session'),
              (SELECT start_local_date FROM plan_assignments WHERE id='legacy-assignment'),
              (SELECT status FROM outbox WHERE id='legacy-outbox'),
              (SELECT COUNT(*) FROM exercises WHERE id='legacy-exercise'),
              (SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='daily_readiness'),
              (SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='cardio_sessions'),
              (SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='equipment_cache'),
              (SELECT COUNT(*) FROM workout_sets WHERE id='legacy-set' AND is_warmup=0);
            """;
        await using var reader = await verify.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal("2026-07-01", reader.GetString(1));
        Assert.Equal("pending", reader.GetString(2));
        Assert.Equal(1L, reader.GetInt64(3));
        Assert.Equal(1L, reader.GetInt64(4));
        Assert.Equal(1L, reader.GetInt64(5));
        Assert.Equal(1L, reader.GetInt64(6));
        Assert.Equal(1L, reader.GetInt64(7));
    }

    [Fact]
    public async Task InitializeAsync_UpgradesV7Rows_WithSafeV8SyncDefaults()
    {
        using var temporary = new TemporaryDirectory("v7 到 v8 同步字段");
        var paths = new AppPaths(temporary.Path);
        paths.EnsureCreated();
        await using (var legacy = new Microsoft.Data.Sqlite.SqliteConnection(
                         $"Data Source={paths.DatabasePath};Pooling=False"))
        {
            await legacy.OpenAsync();
            await using var create = legacy.CreateCommand();
            create.CommandText = """
                CREATE TABLE schema_migrations(version INTEGER NOT NULL PRIMARY KEY, applied_at TEXT NOT NULL);
                INSERT INTO schema_migrations(version, applied_at) VALUES
                    (1,'2026-01-01T00:00:00Z'),(2,'2026-01-02T00:00:00Z'),
                    (3,'2026-01-03T00:00:00Z'),(4,'2026-01-04T00:00:00Z'),
                    (5,'2026-01-05T00:00:00Z'),(6,'2026-01-06T00:00:00Z'),
                    (7,'2026-01-07T00:00:00Z');

                CREATE TABLE plans(id TEXT NOT NULL PRIMARY KEY);
                INSERT INTO plans(id) VALUES ('v7-plan');

                CREATE TABLE workout_sessions(
                    id TEXT NOT NULL PRIMARY KEY,
                    source TEXT NOT NULL DEFAULT 'windows',
                    deleted_at TEXT NULL);
                INSERT INTO workout_sessions(id, source, deleted_at)
                    VALUES ('v7-session', 'android', NULL);

                CREATE TABLE workout_sets(
                    id TEXT NOT NULL PRIMARY KEY,
                    equipment_key TEXT NOT NULL DEFAULT '');
                INSERT INTO workout_sets(id, equipment_key) VALUES ('v7-set', '史密斯机');

                CREATE TABLE daily_readiness(id TEXT NOT NULL PRIMARY KEY);
                INSERT INTO daily_readiness(id) VALUES ('v7-readiness');

                CREATE TABLE cardio_sessions(id TEXT NOT NULL PRIMARY KEY);
                INSERT INTO cardio_sessions(id) VALUES ('v7-cardio');
                """;
            await create.ExecuteNonQueryAsync();
        }

        var database = new SqliteDatabase(paths);
        await database.InitializeAsync();

        Assert.Equal(LatestSchemaVersion, await database.GetSchemaVersionAsync());
        await using var connection = await database.OpenConnectionAsync();
        await using var verify = connection.CreateCommand();
        verify.CommandText = """
            SELECT
              (SELECT entity_version FROM plans WHERE id='v7-plan'),
              (SELECT source FROM workout_sessions WHERE id='v7-session'),
              (SELECT timezone FROM workout_sessions WHERE id='v7-session'),
              (SELECT entity_version FROM workout_sessions WHERE id='v7-session'),
              (SELECT equipment_id IS NULL FROM workout_sets WHERE id='v7-set'),
              (SELECT entity_version FROM workout_sets WHERE id='v7-set'),
              (SELECT entity_version FROM daily_readiness WHERE id='v7-readiness'),
              (SELECT source FROM cardio_sessions WHERE id='v7-cardio'),
              (SELECT entity_version FROM cardio_sessions WHERE id='v7-cardio'),
              (SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='user_cache');
            """;
        await using var reader = await verify.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(0L, reader.GetInt64(0));
        Assert.Equal("android", reader.GetString(1));
        Assert.Equal("UTC", reader.GetString(2));
        Assert.Equal(0L, reader.GetInt64(3));
        Assert.Equal(1L, reader.GetInt64(4));
        Assert.Equal(0L, reader.GetInt64(5));
        Assert.Equal(0L, reader.GetInt64(6));
        Assert.Equal("windows", reader.GetString(7));
        Assert.Equal(0L, reader.GetInt64(8));
        Assert.Equal(1L, reader.GetInt64(9));
        Assert.NotEmpty(Directory.EnumerateFiles(paths.BackupsDirectory, "pre-migration-v7-*.db"));
    }

    [Fact]
    public void AppPaths_ExpandsAndNormalizesUnicodeDataDirectory()
    {
        using var temporary = new TemporaryDirectory("训练 记录 路径");
        var paths = new AppPaths(temporary.Path + Path.DirectorySeparatorChar);

        paths.EnsureCreated();

        Assert.Equal(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(temporary.Path)),
            Path.TrimEndingDirectorySeparator(paths.DataDirectory));
        Assert.Equal(Path.Combine(paths.DataDirectory, "fitness.db"), paths.DatabasePath);
        Assert.All(
            new[] { paths.LogsDirectory, paths.CacheDirectory, paths.BackupsDirectory },
            directory => Assert.True(Directory.Exists(directory)));
    }
}

internal sealed class TemporaryDirectory : IDisposable
{
    private readonly string _root;

    public TemporaryDirectory(string label)
    {
        _root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "PersonalFitnessPlanner.Tests",
            label,
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public string Path => _root;

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
