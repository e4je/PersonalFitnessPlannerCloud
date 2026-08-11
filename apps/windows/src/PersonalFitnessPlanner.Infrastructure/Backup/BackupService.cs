using Microsoft.Data.Sqlite;
using PersonalFitnessPlanner.Infrastructure.Persistence;

namespace PersonalFitnessPlanner.Infrastructure.Backup;

public sealed class BackupService
{
    public const int RetainedBackupCount = 10;
    private readonly SqliteDatabase _database;
    private readonly AppPaths _paths;

    public BackupService(SqliteDatabase database, AppPaths paths)
    {
        _database = database;
        _paths = paths;
    }

    public async Task<string> CreateBackupAsync(CancellationToken cancellationToken = default)
    {
        await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        _paths.EnsureCreated();
        var backupPath = GetUnusedBackupPath();
        var destinationString = new SqliteConnectionStringBuilder
        {
            DataSource = backupPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();
        await using var source = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var destination = new SqliteConnection(destinationString);
        await destination.OpenAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        source.BackupDatabase(destination);
        await destination.CloseAsync().ConfigureAwait(false);
        await source.CloseAsync().ConfigureAwait(false);
        PruneOldBackups(backupPath);
        return backupPath;
    }

    private string GetUnusedBackupPath()
    {
        var baseName = $"fitness-{DateTime.Now:yyyyMMdd-HHmmss}";
        var path = Path.Combine(_paths.BackupsDirectory, baseName + ".db");
        for (var suffix = 1; File.Exists(path); suffix++)
        {
            path = Path.Combine(_paths.BackupsDirectory, $"{baseName}-{suffix}.db");
        }
        return path;
    }

    private void PruneOldBackups(string justCreated)
    {
        var backups = new DirectoryInfo(_paths.BackupsDirectory)
            .EnumerateFiles("*.db", SearchOption.TopDirectoryOnly)
            .OrderByDescending(x => string.Equals(x.FullName, justCreated, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(x => x.LastWriteTimeUtc)
            .ToArray();
        foreach (var oldBackup in backups.Skip(RetainedBackupCount))
        {
            oldBackup.Delete();
        }
    }
}
