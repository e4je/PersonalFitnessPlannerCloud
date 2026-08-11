using System.Text.Json;
using PersonalFitnessPlanner.Infrastructure;
using PersonalFitnessPlanner.Infrastructure.Models;
using PersonalFitnessPlanner.Infrastructure.Persistence;

namespace PersonalFitnessPlanner.Tests;

public sealed class FitnessRepositoryTests
{
    [Fact]
    public async Task SaveSet_IsIdempotent_AndOutboxCommitsExactlyOnce()
    {
        using var temporary = new TemporaryDirectory("幂等 Outbox 数据");
        var (repository, database) = await CreateRepositoryAsync(temporary.Path);
        var workout = await repository.StartWorkoutAsync("A", new DateOnly(2026, 8, 10));
        var item = workout.Snapshot.Days.Single(day => day.Code == "A").Items.OrderBy(x => x.Position).First();
        var option = item.Options.Single(x => x.IsPreferred);
        const string clientSetKey = "windows:test:set:stable-key";
        var input = new SaveSetInput(
            workout.SessionId,
            item.Id,
            option,
            SetNumber: 1,
            WeightKg: 40m,
            Reps: 10,
            DurationSeconds: null,
            Rir: 2,
            Pain: false,
            Notes: "动作稳定",
            ClientSetKey: clientSetKey);

        var first = await repository.SaveSetAsync(input);
        var duplicate = await repository.SaveSetAsync(input);
        var resumed = await repository.GetActiveWorkoutAsync();

        Assert.True(first);
        Assert.False(duplicate);
        Assert.NotNull(resumed);
        Assert.Single(resumed.SavedSets);

        var pending = await repository.GetPendingOutboxAsync();
        var sessionMessage = Assert.Single(pending, pendingItem =>
            pendingItem.EntityType == "workout_session" && pendingItem.EntityId == workout.SessionId);
        using (var sessionPayload = JsonDocument.Parse(sessionMessage.PayloadJson))
        {
            var root = sessionPayload.RootElement;
            Assert.Equal(workout.SessionId, root.GetProperty("id").GetGuid());
            Assert.Equal("A", root.GetProperty("plan_day_code").GetString());
            Assert.Equal("2026-08-10", root.GetProperty("local_date").GetString());
            Assert.Equal("UTC", root.GetProperty("timezone").GetString());
            Assert.Equal("IN_PROGRESS", root.GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.String, root.GetProperty("plan_snapshot_json").ValueKind);
            Assert.False(root.TryGetProperty("dayCode", out _));
            Assert.False(root.TryGetProperty("planVersionId", out _));
        }

        var setMessage = Assert.Single(pending, item => item.IdempotencyKey == clientSetKey);
        using (var setPayload = JsonDocument.Parse(setMessage.PayloadJson))
        {
            var root = setPayload.RootElement;
            Assert.Equal(workout.SessionId, root.GetProperty("session_id").GetGuid());
            Assert.Equal(item.Id, root.GetProperty("plan_slot_id").GetGuid());
            Assert.Equal(option.Id, root.GetProperty("source_plan_slot_option_id").GetGuid());
            Assert.Equal(option.ExerciseId, root.GetProperty("exercise_id").GetGuid());
            Assert.Equal(option.EquipmentId, root.GetProperty("equipment_id").GetGuid());
            Assert.Equal(1, root.GetProperty("set_number").GetInt32());
            Assert.True(root.GetProperty("completed").GetBoolean());
            Assert.False(root.TryGetProperty("sessionId", out _));
            Assert.False(root.TryGetProperty("planItemId", out _));
        }
        await repository.MarkOutboxFailedAsync([setMessage.Id], "offline");
        var failedStatus = await repository.GetOutboxStatusAsync();
        Assert.Equal(1, failedStatus.Failed);

        await repository.MarkOutboxSucceededAsync([setMessage.Id, setMessage.Id]);
        var succeededStatus = await repository.GetOutboxStatusAsync();
        Assert.Equal(0, succeededStatus.Failed);
        Assert.Equal(failedStatus.Pending - 1, succeededStatus.Pending);

        Assert.Equal(string.Empty, await repository.GetSyncCursorAsync());
        await repository.SetSyncCursorAsync("cursor-42");
        Assert.Equal("cursor-42", await repository.GetSyncCursorAsync());

        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM outbox WHERE idempotency_key=$key;";
        command.Parameters.AddWithValue("$key", clientSetKey);
        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task ReadinessOutbox_UsesCanonicalPayloadAndStableDateIdentity()
    {
        using var temporary = new TemporaryDirectory("每日状态 canonical Outbox");
        var (repository, _) = await CreateRepositoryAsync(temporary.Path);
        var localDate = new DateOnly(2026, 8, 10);
        var firstId = Guid.NewGuid();
        var replacementId = Guid.NewGuid();

        await repository.SaveReadinessAsync(new DailyReadinessData(
            firstId, localDate, 4, 5, string.Empty, "第一次"));
        await repository.SaveReadinessAsync(new DailyReadinessData(
            replacementId, localDate, 7, 3, "右肩", "第二次"));

        var latest = await repository.GetLatestReadinessAsync();
        Assert.NotNull(latest);
        Assert.Equal(firstId, latest.Id);
        Assert.Equal(7, latest.FatigueScore);

        var messages = (await repository.GetPendingOutboxAsync())
            .Where(item => item.EntityType == "daily_readiness")
            .ToArray();
        Assert.Equal(2, messages.Length);
        Assert.All(messages, message => Assert.Equal(firstId, message.EntityId));
        Assert.Equal(2, messages.Select(message => message.IdempotencyKey).Distinct().Count());

        using var payload = JsonDocument.Parse(messages.Last().PayloadJson);
        var root = payload.RootElement;
        Assert.Equal(firstId, root.GetProperty("id").GetGuid());
        Assert.Equal("2026-08-10", root.GetProperty("local_date").GetString());
        Assert.Equal(7, root.GetProperty("fatigue_score").GetInt32());
        Assert.Equal(3, root.GetProperty("sleep_quality").GetInt32());
        Assert.Equal("右肩", root.GetProperty("pain_notes").GetString());
        Assert.Equal(JsonValueKind.Object, root.GetProperty("metrics").ValueKind);
        Assert.False(root.TryGetProperty("localDate", out _));
        Assert.False(root.TryGetProperty("fatigueScore", out _));
    }

    [Fact]
    public async Task ActiveWorkout_ResumesAfterRepositoryRestart_WithOriginalSnapshot()
    {
        using var temporary = new TemporaryDirectory("异常关闭 恢复");
        var paths = new AppPaths(temporary.Path);
        var database = new SqliteDatabase(paths);
        var firstRepository = new FitnessRepository(database);
        await firstRepository.InitializeAsync();
        var started = await firstRepository.StartWorkoutAsync("B", new DateOnly(2026, 8, 11));

        var restartedRepository = new FitnessRepository(new SqliteDatabase(new AppPaths(temporary.Path)));
        await restartedRepository.InitializeAsync();
        var resumed = await restartedRepository.GetActiveWorkoutAsync();
        var startAgain = await restartedRepository.StartWorkoutAsync("A", new DateOnly(2026, 8, 12));

        Assert.NotNull(resumed);
        Assert.Equal(started.SessionId, resumed.SessionId);
        Assert.Equal("B", resumed.DayCode);
        Assert.Equal(started.Snapshot.Id, resumed.Snapshot.Id);
        Assert.Equal(started.SessionId, startAgain.SessionId);
    }

    [Fact]
    public async Task SavedSetEquipmentId_SurvivesRestart_AndIsReusedByUpdateOutbox()
    {
        using var temporary = new TemporaryDirectory("器械 UUID 重启持久化");
        var paths = new AppPaths(temporary.Path);
        var firstRepository = new FitnessRepository(new SqliteDatabase(paths));
        await firstRepository.InitializeAsync();
        var workout = await firstRepository.StartWorkoutAsync("A", new DateOnly(2026, 8, 13));
        var item = workout.Snapshot.Days.Single(day => day.Code == "A").Items.OrderBy(value => value.Position).First();
        var option = item.Options.Single(value => value.IsPreferred);
        Assert.NotNull(option.EquipmentId);
        await firstRepository.SaveSetAsync(new SaveSetInput(
            workout.SessionId, item.Id, option, 1, 40m, 10, null, 2, false, string.Empty,
            "equipment-persistence:first"));

        var restarted = new FitnessRepository(new SqliteDatabase(paths));
        await restarted.InitializeAsync();
        var updated = await restarted.UpdatePreviousSetAsync(
            workout.SessionId, item.Id, 42.5m, 9, 2, false);

        Assert.NotNull(updated);
        Assert.Equal(option.EquipmentId, updated.EquipmentId);
        var updateMessage = Assert.Single(
            await restarted.GetPendingOutboxAsync(),
            value => value.IdempotencyKey.Contains(":update:", StringComparison.Ordinal));
        using var payload = JsonDocument.Parse(updateMessage.PayloadJson);
        Assert.Equal(option.EquipmentId, payload.RootElement.GetProperty("equipment_id").GetGuid());
    }

    [Fact]
    public async Task PublishedPlan_IsImmutable_AndHistoryKeepsWorkoutSnapshot()
    {
        using var temporary = new TemporaryDirectory("计划版本 历史快照");
        var (repository, _) = await CreateRepositoryAsync(temporary.Path);
        var original = await repository.GetCurrentPlanAsync();
        var workout = await repository.StartWorkoutAsync("A", new DateOnly(2026, 8, 10));
        await repository.CompleteWorkoutAsync(workout.SessionId, endedEarly: false);

        var draft = await repository.CreatePlanDraftAsync();
        var renamedDraft = draft with { Name = "新周期计划" };
        await repository.SavePlanDraftAsync(renamedDraft);
        var published = await repository.PublishPlanAsync(renamedDraft);
        await repository.AssignPlanAsync(published.Id);

        var current = await repository.GetCurrentPlanAsync();
        var history = await repository.GetHistoryAsync();
        var versions = await repository.GetPlanVersionsAsync();

        Assert.Equal("published", published.Status, ignoreCase: true);
        Assert.Equal(original.Version + 1, published.Version);
        Assert.Equal(original.PlanId, published.PlanId);
        Assert.NotEqual(original.Id, published.Id);
        Assert.Equal(published.Id, current.Id);
        Assert.Contains(versions, plan => plan.Id == original.Id);
        Assert.Contains(versions, plan => plan.Id == published.Id);
        Assert.Single(history);
        Assert.Equal($"{original.Name} v{original.Version}", history[0].PlanVersion);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.SavePlanDraftAsync(published with { Name = "非法原地修改" }));
    }

    [Fact]
    public async Task SoftDelete_RemovesWorkoutFromHistoryWithoutPhysicalDeletion()
    {
        using var temporary = new TemporaryDirectory("软删除 历史");
        var (repository, database) = await CreateRepositoryAsync(temporary.Path);
        var workout = await repository.StartWorkoutAsync("B", new DateOnly(2026, 8, 12));
        await repository.CompleteWorkoutAsync(workout.SessionId, endedEarly: true);
        Assert.Single(await repository.GetHistoryAsync());

        await repository.SoftDeleteWorkoutAsync(workout.SessionId);

        Assert.Empty(await repository.GetHistoryAsync());
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM workout_sessions WHERE id=$id AND deleted_at IS NOT NULL;";
        command.Parameters.AddWithValue("$id", workout.SessionId.ToString("D"));
        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task WeightSuggestion_UsesLatestExactOptionHistory_AndPainPreventsIncrease()
    {
        using var temporary = new TemporaryDirectory("重量建议 生产链");
        using var service = new AppDataService(new AppPaths(temporary.Path));
        await service.InitializeAsync();
        var plan = await service.GetCurrentPlanAsync();
        var item = plan.Days.Single(day => day.Code == "A").Items.OrderBy(x => x.Position).First();
        var preferred = item.Options.Single(option => option.IsPreferred);
        var alternative = item.Options.First(option => !option.IsPreferred);
        var firstWorkout = await service.StartWorkoutAsync("A");
        for (var setNumber = 1; setNumber <= 3; setNumber++)
        {
            await service.SaveSetAsync(new SaveSetInput(
                firstWorkout.SessionId,
                item.Id,
                preferred,
                setNumber,
                WeightKg: 40m,
                Reps: preferred.RepMax,
                DurationSeconds: null,
                Rir: 2,
                Pain: false,
                Notes: string.Empty,
                ClientSetKey: $"weight:first:{setNumber}"));
        }
        await service.CompleteWorkoutAsync(firstWorkout.SessionId, endedEarly: false);

        var increase = await service.GetWeightSuggestionAsync(preferred);
        var isolatedAlternative = await service.GetWeightSuggestionAsync(alternative);

        Assert.Equal(40m, increase.LastWeightKg);
        Assert.Equal(42.5m, increase.SuggestedWeightKg);
        Assert.Equal("Increase", increase.Action);
        Assert.Null(isolatedAlternative.LastWeightKg);
        Assert.Equal("NoHistory", isolatedAlternative.Reason);

        var painfulWorkout = await service.StartWorkoutAsync("A");
        await service.SaveSetAsync(new SaveSetInput(
            painfulWorkout.SessionId,
            item.Id,
            preferred,
            SetNumber: 1,
            WeightKg: 42.5m,
            Reps: preferred.RepMax,
            DurationSeconds: null,
            Rir: 2,
            Pain: true,
            Notes: "肩部疼痛",
            ClientSetKey: "weight:painful:1"));
        await service.CompleteWorkoutAsync(painfulWorkout.SessionId, endedEarly: false);

        var pain = await service.GetWeightSuggestionAsync(preferred);

        Assert.True(pain.PainReported);
        Assert.Equal(42.5m, pain.SuggestedWeightKg);
        Assert.Equal("Hold", pain.Action);
        Assert.Equal("PainReported", pain.Reason);
    }

    [Fact]
    public async Task Dashboard_UsesAssignedPlanRulesInsteadOfLocalTrainingDayCount()
    {
        using var temporary = new TemporaryDirectory("仪表盘 使用云端计划规则");
        using var service = new AppDataService(new AppPaths(temporary.Path));
        await service.InitializeAsync();
        var draft = await service.Repository.CreatePlanDraftAsync();
        var configured = draft with
        {
            WeeklyStrengthTarget = 5,
            MinimumRestDays = 2,
            FatigueThreshold = 6
        };
        await service.Repository.SavePlanDraftAsync(configured);
        var published = await service.Repository.PublishPlanAsync(configured, enqueueOutbox: false);
        await service.Repository.AssignPlanAsync(
            published.Id,
            Guid.NewGuid(),
            enqueueOutbox: false);
        var settings = await service.Settings.GetAsync();
        await service.Settings.SaveAsync(settings with { TrainingDays = "1" });

        var dashboard = await service.GetDashboardAsync();
        var current = await service.GetCurrentPlanAsync();

        Assert.Equal(5, dashboard.WeeklyTarget);
        Assert.Equal(5, current.WeeklyStrengthTarget);
        Assert.Equal(2, current.MinimumRestDays);
        Assert.Equal(6, current.FatigueThreshold);
    }

    private static async Task<(FitnessRepository Repository, SqliteDatabase Database)> CreateRepositoryAsync(string path)
    {
        var database = new SqliteDatabase(new AppPaths(path));
        var repository = new FitnessRepository(database);
        await repository.InitializeAsync();
        return (repository, database);
    }
}
