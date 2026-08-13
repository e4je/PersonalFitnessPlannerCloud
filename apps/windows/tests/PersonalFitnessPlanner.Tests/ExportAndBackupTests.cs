using System.Net;
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

    [Theory]
    [InlineData("=2+3")]
    [InlineData("  +2+3")]
    [InlineData("\t-2+3")]
    [InlineData(" @SUM(1,2)")]
    public async Task CsvExport_NeutralizesFormulaPrefixesAfterWhitespace(string exerciseName)
    {
        using var temporary = new TemporaryDirectory("CSV 公式注入");
        var paths = new AppPaths(temporary.Path);
        var repository = new FitnessRepository(new SqliteDatabase(paths));
        await repository.InitializeAsync();
        var draft = await repository.CreatePlanDraftAsync();
        var firstDay = draft.Days.First();
        var firstItem = firstDay.Items.OrderBy(item => item.Position).First();
        var preferred = firstItem.Options.Single(option => option.IsPreferred) with { ExerciseName = exerciseName };
        var changedItem = firstItem with
        {
            Options = firstItem.Options.Select(option => option.Id == preferred.Id ? preferred : option).ToArray()
        };
        var changedDay = firstDay with
        {
            Items = firstDay.Items.Select(item => item.Id == changedItem.Id ? changedItem : item).ToArray()
        };
        draft = draft with
        {
            Days = draft.Days.Select(day => day.Code == changedDay.Code ? changedDay : day).ToArray()
        };
        await repository.SavePlanDraftAsync(draft);
        var published = await repository.PublishPlanAsync(draft);
        await repository.AssignPlanAsync(published.Id);
        var workout = await repository.StartWorkoutAsync(changedDay.Code, new DateOnly(2026, 8, 13));
        await repository.SaveSetAsync(new SaveSetInput(
            workout.SessionId,
            changedItem.Id,
            preferred,
            1,
            10,
            10,
            null,
            2,
            false,
            string.Empty,
            $"csv:{Guid.NewGuid():D}"));
        await repository.CompleteWorkoutAsync(workout.SessionId, endedEarly: false);

        var exporter = new ExportService(repository, new SettingsStore(paths));
        var csvPath = await exporter.ExportHistoryCsvAsync(Path.Combine(temporary.Path, "exports"));
        var csv = await File.ReadAllTextAsync(csvPath, Encoding.UTF8);

        Assert.Contains("'" + exerciseName, csv);
    }

    [Fact]
    public async Task JsonImport_PreservesMachineSettingsAndCredentialOriginAcrossRestart()
    {
        using var temporary = new TemporaryDirectory("恶意导入重启链");
        var paths = new AppPaths(temporary.Path);
        var originalSettings = AppSettingsData.Default(paths.DataDirectory) with
        {
            ApiBaseUrl = "https://fitness.example.com/api/",
            Theme = "dark"
        };
        await new SettingsStore(paths).SaveAsync(originalSettings);
        var accessToken = CreateJwt(new
        {
            sub = "import-user",
            role = "user",
            exp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()
        });
        var loginHandler = new ImportRecordingHandler(request =>
            request.RequestUri!.AbsolutePath.EndsWith("/auth/login", StringComparison.Ordinal)
                ? JsonResponse(HttpStatusCode.OK, JsonSerializer.Serialize(new
                {
                    access_token = accessToken,
                    refresh_token = "refresh-import",
                    expires_in = 3600
                }))
                : JsonResponse(HttpStatusCode.OK, "{\"changes\":[],\"next_cursor\":\"\"}"));

        string exportPath;
        using (var service = new AppDataService(paths, new HttpClient(loginHandler)))
        {
            await service.InitializeAsync();
            await service.LoginAsync("import@example.com", "password");
            exportPath = await service.ExportDataJsonAsync(Path.Combine(temporary.Path, "exports"));
            var json = await File.ReadAllTextAsync(exportPath);
            json = json.Replace(
                    "https://fitness.example.com/api/",
                    "https://attacker.example.net/",
                    StringComparison.Ordinal)
                .Replace(paths.DataDirectory.Replace("\\", "\\\\", StringComparison.Ordinal),
                    "C:\\\\attacker-controlled", StringComparison.Ordinal);
            await File.WriteAllTextAsync(exportPath, json, Encoding.UTF8);
            await service.ImportDataJsonAsync(exportPath);

            var settings = await service.GetSettingsAsync();
            Assert.Equal(originalSettings.ApiBaseUrl, settings.ApiBaseUrl);
            Assert.Equal(originalSettings.DataDirectory, settings.DataDirectory);
            Assert.NotNull(await service.Tokens.LoadAsync());
        }

        var restartHandler = new ImportRecordingHandler(_ =>
            JsonResponse(HttpStatusCode.OK, "{\"changes\":[],\"next_cursor\":\"restart-cursor\"}"));
        using (var restarted = new AppDataService(paths, new HttpClient(restartHandler)))
        {
            await restarted.InitializeAsync();
            var page = await restarted.ApiClient.GetChangesAsync(string.Empty);

            Assert.Equal("restart-cursor", page.Cursor);
            var request = Assert.Single(restartHandler.Requests);
            Assert.Equal("fitness.example.com", request.RequestUri!.Host);
            Assert.Equal("Bearer " + accessToken, request.Headers.Authorization?.ToString());
        }
    }

    [Fact]
    public async Task JsonImport_RejectsOversizedFileBeforeDeserialization()
    {
        using var temporary = new TemporaryDirectory("过大 JSON 导入");
        var paths = new AppPaths(temporary.Path);
        var repository = new FitnessRepository(new SqliteDatabase(paths));
        await repository.InitializeAsync();
        var filePath = Path.Combine(temporary.Path, "oversized.json");
        await using (var stream = new FileStream(filePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.SetLength(ExportService.MaxImportFileBytes + 1);
        }

        var importer = new ExportService(repository, new SettingsStore(paths));
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => importer.ImportDataJsonAsync(filePath));

        Assert.Contains("64 MiB", exception.Message);
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

    private static string CreateJwt(object payload) =>
        Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "none", typ = "JWT" })) + "." +
        Base64Url(JsonSerializer.SerializeToUtf8Bytes(payload)) + ".signature";

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class ImportRecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _response;

        public ImportRecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> response) => _response = response;

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(_response(request));
        }
    }
}
