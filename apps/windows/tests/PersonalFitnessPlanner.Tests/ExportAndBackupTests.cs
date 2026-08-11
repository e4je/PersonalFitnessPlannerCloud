using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PersonalFitnessPlanner.Infrastructure;
using PersonalFitnessPlanner.Infrastructure.Backup;
using PersonalFitnessPlanner.Infrastructure.Export;
using PersonalFitnessPlanner.Infrastructure.Models;
using PersonalFitnessPlanner.Infrastructure.Persistence;

namespace PersonalFitnessPlanner.Tests;

public sealed class ExportAndBackupTests
{
    [Fact]
    public async Task CsvAndJsonExport_AreUnicodeComplete_AndJsonCanRestoreSnapshot()
    {
        using var sourceTemporary = new TemporaryDirectory("导出 源数据");
        var sourcePaths = new AppPaths(sourceTemporary.Path);
        var sourceDatabase = new SqliteDatabase(sourcePaths);
        var sourceRepository = new FitnessRepository(sourceDatabase);
        await sourceRepository.InitializeAsync();
        var workout = await sourceRepository.StartWorkoutAsync("A", new DateOnly(2026, 8, 9));
        var item = workout.Snapshot.Days.Single(day => day.Code == "A").Items.OrderBy(x => x.Position).First();
        var option = item.Options.Single(x => x.IsPreferred);
        await sourceRepository.SaveSetAsync(new SaveSetInput(
            workout.SessionId,
            item.Id,
            option,
            SetNumber: 1,
            WeightKg: 40m,
            Reps: 10,
            DurationSeconds: null,
            Rir: 2,
            Pain: false,
            Notes: "中文备注，含逗号",
            ClientSetKey: "export:set:1"));
        await sourceRepository.CompleteWorkoutAsync(workout.SessionId, endedEarly: false);

        var exportDirectory = Path.Combine(sourceTemporary.Path, "导出 文件 目录");
        var exporter = new ExportService(sourceRepository, new SettingsStore(sourcePaths));
        var csvPath = await exporter.ExportHistoryCsvAsync(exportDirectory);
        var jsonPath = await exporter.ExportDataJsonAsync(exportDirectory);

        Assert.Equal(exportDirectory, Path.GetDirectoryName(csvPath));
        Assert.Equal(exportDirectory, Path.GetDirectoryName(jsonPath));
        Assert.Contains("训练历史-", Path.GetFileName(csvPath));
        Assert.Contains("健身数据-", Path.GetFileName(jsonPath));

        var csvBytes = await File.ReadAllBytesAsync(csvPath);
        Assert.True(csvBytes.Length > 3);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, csvBytes[..3]);
        var csv = Encoding.UTF8.GetString(csvBytes);
        Assert.Contains("日期,A/B,来源,状态,组数,最高重量(kg),总次数,总容量(kg)", csv);
        Assert.Contains("2026-08-09,A,windows,completed,1,40,10,400", csv);
        Assert.Contains(option.ExerciseName, csv);

        await using (var jsonStream = File.OpenRead(jsonPath))
        using (var document = await JsonDocument.ParseAsync(jsonStream))
        {
            var root = document.RootElement;
            Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
            Assert.Single(root.GetProperty("workoutSessions").EnumerateArray());
            var exportedSession = root.GetProperty("workoutSessions")[0];
            Assert.Equal(workout.SessionId, exportedSession.GetProperty("id").GetGuid());
            Assert.Equal(workout.Snapshot.Id, exportedSession.GetProperty("planSnapshot").GetProperty("id").GetGuid());
            Assert.Single(exportedSession.GetProperty("sets").EnumerateArray());
        }

        using var restoredTemporary = new TemporaryDirectory("JSON 恢复 数据");
        var restoredPaths = new AppPaths(restoredTemporary.Path);
        var restoredRepository = new FitnessRepository(new SqliteDatabase(restoredPaths));
        await restoredRepository.InitializeAsync();
        var importer = new ExportService(restoredRepository, new SettingsStore(restoredPaths));
        await importer.ImportDataJsonAsync(jsonPath);

        var restored = Assert.Single(await restoredRepository.GetWorkoutExportSessionsAsync());
        Assert.Equal(workout.SessionId, restored.Id);
        Assert.Equal(workout.Snapshot.Id, restored.PlanSnapshot.Id);
        Assert.Single(restored.Sets);
        Assert.Equal(40m, restored.Sets[0].WeightKg);
    }

    [Fact]
    public async Task Backup_UsesConsistentSqliteCopy_AndRetainsNewestTen()
    {
        using var temporary = new TemporaryDirectory("备份 轮换 数据");
        var paths = new AppPaths(temporary.Path);
        var database = new SqliteDatabase(paths);
        await new FitnessRepository(database).InitializeAsync();
        var backup = new BackupService(database, paths);

        string latest = string.Empty;
        for (var index = 0; index < BackupService.RetainedBackupCount + 2; index++)
        {
            latest = await backup.CreateBackupAsync();
        }

        var files = Directory.GetFiles(paths.BackupsDirectory, "fitness-*.db");
        Assert.Equal(BackupService.RetainedBackupCount, files.Length);
        Assert.Contains(latest, files);

        await using var connection = new SqliteConnection($"Data Source={latest};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        Assert.Equal("ok", await command.ExecuteScalarAsync());
    }
}
