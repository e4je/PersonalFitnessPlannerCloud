using PersonalFitnessPlanner.Contracts;
using PersonalFitnessPlanner.Infrastructure;
using PersonalFitnessPlanner.Infrastructure.Models;
using PersonalFitnessPlanner.Infrastructure.Persistence;
using System.Text.Json.Nodes;

namespace PersonalFitnessPlanner.Tests;

public sealed class CloudSyncPersistenceTests
{
    [Fact]
    public async Task BootstrapCurrentPlanWithoutAssignment_UsesStableLocalFallbackWithoutOutbox()
    {
        using var temporary = new TemporaryDirectory("首次用户 current plan fallback");
        var database = new SqliteDatabase(new AppPaths(temporary.Path));
        var repository = new FitnessRepository(database);
        await repository.InitializeAsync();
        var remotePlanId = Guid.NewGuid();
        var remoteUserId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 8, 9, 8, 0, 0, TimeSpan.Zero);
        var bootstrap = new BootstrapDto
        {
            User = new UserDto
            {
                Id = remoteUserId,
                Email = "first@example.com",
                DisplayName = "首次用户",
                Version = 1,
                UpdatedAt = now
            },
            CurrentPlan = new PlanVersionDto
            {
                Id = remotePlanId,
                PlanId = Guid.NewGuid(),
                PlanName = "用户默认云端计划",
                VersionNumber = 1,
                Status = "published",
                Version = 1,
                UpdatedAt = now
            }
        };

        await repository.ApplyBootstrapAsync(ContractJson.Serialize(bootstrap));
        var firstAssignmentId = await ActiveAssignmentIdAsync();
        await repository.ApplyBootstrapAsync(ContractJson.Serialize(bootstrap));
        var secondAssignmentId = await ActiveAssignmentIdAsync();

        Assert.Equal(remotePlanId, (await repository.GetCurrentPlanAsync()).Id);
        Assert.Equal(firstAssignmentId, secondAssignmentId);
        Assert.NotEqual(Guid.Empty, firstAssignmentId);
        await using var connection = await database.OpenConnectionAsync();
        await using var verify = connection.CreateCommand();
        verify.CommandText = """
            SELECT
              (SELECT COUNT(*) FROM plan_assignments WHERE is_active=1),
              (SELECT COUNT(*) FROM outbox WHERE entity_type IN ('assignment','plan_assignment'));
            """;
        await using var reader = await verify.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal(0L, reader.GetInt64(1));

        async Task<Guid> ActiveAssignmentIdAsync()
        {
            await using var connection = await database.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT id FROM plan_assignments WHERE is_active=1;";
            return Guid.Parse((string)(await command.ExecuteScalarAsync())!);
        }
    }

    [Fact]
    public async Task Bootstrap_PersistsAssignmentsForCurrentAndHistoricalPlanVersions()
    {
        using var temporary = new TemporaryDirectory("bootstrap 全部分配计划版本");
        var database = new SqliteDatabase(new AppPaths(temporary.Path));
        var repository = new FitnessRepository(database);
        await repository.InitializeAsync();
        var userId = Guid.NewGuid();
        var logicalPlanId = Guid.NewGuid();
        var currentVersionId = Guid.NewGuid();
        var historicalVersionId = Guid.NewGuid();
        var currentPlan = new PlanVersionDto
        {
            Id = currentVersionId,
            PlanId = logicalPlanId,
            PlanName = "云端计划",
            VersionNumber = 2,
            Status = "published",
            Version = 5
        };
        var historicalPlan = new PlanVersionDto
        {
            Id = historicalVersionId,
            PlanId = logicalPlanId,
            PlanName = "云端计划",
            VersionNumber = 1,
            Status = "archived",
            Version = 3
        };

        await repository.ApplyFullBootstrapAsync(ContractJson.Serialize(new BootstrapDto
        {
            User = new UserDto { Id = userId, Email = "athlete@example.test", Version = 1 },
            CurrentPlan = currentPlan,
            PlanVersions = [historicalPlan, currentPlan],
            Assignments =
            [
                new PlanAssignmentDto
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    PlanVersionId = historicalVersionId,
                    StartLocalDate = new DateOnly(2026, 7, 1),
                    EndLocalDate = new DateOnly(2026, 8, 8),
                    IsActive = false,
                    Version = 2
                },
                new PlanAssignmentDto
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    PlanVersionId = currentVersionId,
                    StartLocalDate = new DateOnly(2026, 8, 9),
                    IsActive = true,
                    Version = 2
                }
            ]
        }));

        await using var connection = await database.OpenConnectionAsync();
        await using var verify = connection.CreateCommand();
        verify.CommandText = """
            SELECT
              (SELECT COUNT(*) FROM plans WHERE id IN ($current,$historical) AND deleted_at IS NULL),
              (SELECT COUNT(*) FROM plan_assignments WHERE plan_version_id IN ($current,$historical)),
              (SELECT COUNT(*) FROM plan_assignments WHERE plan_version_id=$current AND is_active=1);
            """;
        verify.Parameters.AddWithValue("$current", currentVersionId.ToString("D"));
        verify.Parameters.AddWithValue("$historical", historicalVersionId.ToString("D"));
        await using var reader = await verify.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(2L, reader.GetInt64(0));
        Assert.Equal(2L, reader.GetInt64(1));
        Assert.Equal(1L, reader.GetInt64(2));
        Assert.Equal(currentVersionId, (await repository.GetCurrentPlanAsync()).Id);
    }

    [Fact]
    public async Task PlanRules_MapFromRemoteAndLegacySnapshotsUseSafeDefaults()
    {
        using var temporary = new TemporaryDirectory("计划规则 云端与旧快照");
        var database = new SqliteDatabase(new AppPaths(temporary.Path));
        var repository = new FitnessRepository(database);
        await repository.InitializeAsync();
        var defaultPlan = await repository.GetCurrentPlanAsync();

        await using (var connection = await database.OpenConnectionAsync())
        {
            await using var select = connection.CreateCommand();
            select.CommandText = "SELECT snapshot_json FROM plans WHERE id=$id;";
            select.Parameters.AddWithValue("$id", defaultPlan.Id.ToString("D"));
            var snapshot = (string)(await select.ExecuteScalarAsync())!;
            var legacySnapshot = JsonNode.Parse(snapshot)!.AsObject();
            legacySnapshot.Remove("weeklyStrengthTarget");
            legacySnapshot.Remove("minimumRestDays");
            legacySnapshot.Remove("fatigueThreshold");

            await using var update = connection.CreateCommand();
            update.CommandText = "UPDATE plans SET snapshot_json=$snapshot WHERE id=$id;";
            update.Parameters.AddWithValue("$snapshot", legacySnapshot.ToJsonString());
            update.Parameters.AddWithValue("$id", defaultPlan.Id.ToString("D"));
            await update.ExecuteNonQueryAsync();
        }

        var legacyPlan = await repository.GetCurrentPlanAsync();
        Assert.Equal(3, legacyPlan.WeeklyStrengthTarget);
        Assert.Equal(1, legacyPlan.MinimumRestDays);
        Assert.Equal(8, legacyPlan.FatigueThreshold);

        var remoteId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 8, 9, 8, 0, 0, TimeSpan.Zero);
        var remote = new PlanVersionDto
        {
            Id = remoteId,
            PlanId = Guid.NewGuid(),
            PlanName = "云端规则计划",
            VersionNumber = 4,
            Status = "published",
            WeeklyFrequency = 5,
            MinRestDays = 0,
            FatigueThreshold = 6,
            IntroWeeks = 3,
            IntroMaxSets = 1,
            Version = 9,
            UpdatedAt = now
        };

        await repository.ApplyServerChangesAsync(
        [
            new SyncChange("plan_version", remoteId, "UPSERT", ContractJson.Serialize(remote), now, remote.Version)
        ]);

        var restarted = new FitnessRepository(database);
        var persisted = Assert.Single(await restarted.GetPlanVersionsAsync(), item => item.Id == remoteId);
        Assert.Equal(5, persisted.WeeklyStrengthTarget);
        Assert.Equal(0, persisted.MinimumRestDays);
        Assert.Equal(6, persisted.FatigueThreshold);
        Assert.Equal(3, persisted.DeloadWeeks);
        Assert.Equal(1, persisted.DeloadMaxSets);
    }

    [Fact]
    public async Task FullBootstrap_ReplacesRemoteWorkoutWellnessCachesAndProtectsPendingLocalData()
    {
        using var temporary = new TemporaryDirectory("完整 bootstrap 服务器镜像");
        var database = new SqliteDatabase(new AppPaths(temporary.Path));
        var repository = new FitnessRepository(database);
        await repository.InitializeAsync();
        var plan = await repository.GetCurrentPlanAsync();
        var now = new DateTimeOffset(2026, 8, 9, 8, 0, 0, TimeSpan.Zero);
        var remoteSessionId = Guid.NewGuid();
        var remoteReadinessId = Guid.NewGuid();
        var remoteCardioId = Guid.NewGuid();
        await repository.ApplyBootstrapAsync(ContractJson.Serialize(new BootstrapDto
        {
            WorkoutSessions =
            [
                new WorkoutSessionDto
                {
                    Id = remoteSessionId,
                    Source = "web",
                    PlanVersionId = plan.Id,
                    PlanDayCode = "A",
                    LocalDate = new DateOnly(2026, 8, 1),
                    StartedAt = now.AddDays(-8),
                    CompletedAt = now.AddDays(-8).AddHours(1),
                    Status = "COMPLETED",
                    Version = 2,
                    UpdatedAt = now.AddDays(-8)
                }
            ],
            Readiness =
            [
                new ReadinessDto
                {
                    Id = remoteReadinessId,
                    LocalDate = new DateOnly(2026, 8, 1),
                    FatigueScore = 5,
                    Version = 2,
                    UpdatedAt = now.AddDays(-8)
                }
            ],
            CardioSessions =
            [
                new CardioSessionDto
                {
                    Id = remoteCardioId,
                    Source = "web",
                    LocalDate = new DateOnly(2026, 8, 1),
                    Activity = "running",
                    DurationMinutes = 20,
                    StartedAt = now.AddDays(-8),
                    CompletedAt = now.AddDays(-8).AddMinutes(20),
                    Version = 2,
                    UpdatedAt = now.AddDays(-8)
                }
            ]
        }));

        var localWorkout = await repository.StartWorkoutAsync("A", new DateOnly(2026, 8, 9));
        var localReadiness = new DailyReadinessData(
            Guid.NewGuid(), new DateOnly(2026, 8, 9), 4, 5, string.Empty, string.Empty);
        var localCardio = new CardioSessionData(
            Guid.NewGuid(), new DateOnly(2026, 8, 9), "walking", 30, 2.5m,
            now, now.AddMinutes(30), string.Empty);
        await repository.SaveReadinessAsync(localReadiness);
        await repository.SaveCardioSessionAsync(localCardio);

        await repository.ApplyFullBootstrapAsync(ContractJson.Serialize(new BootstrapDto
        {
            WorkoutSessions =
            [
                new WorkoutSessionDto
                {
                    Id = localWorkout.SessionId,
                    Source = "web",
                    PlanVersionId = plan.Id,
                    PlanDayCode = "B",
                    LocalDate = new DateOnly(2026, 8, 8),
                    StartedAt = now.AddDays(-1),
                    Status = "COMPLETED",
                    Version = 99,
                    UpdatedAt = now.AddDays(-1)
                }
            ],
            Readiness =
            [
                new ReadinessDto
                {
                    Id = localReadiness.Id,
                    LocalDate = localReadiness.LocalDate,
                    FatigueScore = 10,
                    Version = 99,
                    UpdatedAt = now
                }
            ],
            CardioSessions =
            [
                new CardioSessionDto
                {
                    Id = localCardio.Id,
                    Source = "web",
                    LocalDate = localCardio.LocalDate,
                    Activity = "running",
                    DurationMinutes = 1,
                    StartedAt = now,
                    Version = 99,
                    UpdatedAt = now
                }
            ],
            SyncCursor = "full-mirror"
        }));

        await using var connection = await database.OpenConnectionAsync();
        await using var verify = connection.CreateCommand();
        verify.CommandText = """
            SELECT
              (SELECT deleted_at IS NOT NULL FROM workout_sessions WHERE id=$remoteSession),
              (SELECT deleted_at IS NOT NULL FROM daily_readiness WHERE id=$remoteReadiness),
              (SELECT deleted_at IS NOT NULL FROM cardio_sessions WHERE id=$remoteCardio),
              (SELECT deleted_at IS NULL FROM workout_sessions WHERE id=$localSession),
              (SELECT deleted_at IS NULL FROM daily_readiness WHERE id=$localReadiness),
              (SELECT deleted_at IS NULL FROM cardio_sessions WHERE id=$localCardio),
              (SELECT source FROM workout_sessions WHERE id=$localSession),
              (SELECT fatigue_score FROM daily_readiness WHERE id=$localReadiness),
              (SELECT duration_minutes FROM cardio_sessions WHERE id=$localCardio);
            """;
        verify.Parameters.AddWithValue("$remoteSession", remoteSessionId.ToString("D"));
        verify.Parameters.AddWithValue("$remoteReadiness", remoteReadinessId.ToString("D"));
        verify.Parameters.AddWithValue("$remoteCardio", remoteCardioId.ToString("D"));
        verify.Parameters.AddWithValue("$localSession", localWorkout.SessionId.ToString("D"));
        verify.Parameters.AddWithValue("$localReadiness", localReadiness.Id.ToString("D"));
        verify.Parameters.AddWithValue("$localCardio", localCardio.Id.ToString("D"));
        await using var reader = await verify.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal(1L, reader.GetInt64(1));
        Assert.Equal(1L, reader.GetInt64(2));
        Assert.Equal(1L, reader.GetInt64(3));
        Assert.Equal(1L, reader.GetInt64(4));
        Assert.Equal(1L, reader.GetInt64(5));
        Assert.Equal("windows", reader.GetString(6));
        Assert.Equal(4L, reader.GetInt64(7));
        Assert.Equal(30L, reader.GetInt64(8));
    }

    [Fact]
    public async Task BootstrapAndIncrementalSync_PersistSupportedEntitiesAndApplyDeletes()
    {
        using var temporary = new TemporaryDirectory("云端实体 bootstrap 与增量");
        var paths = new AppPaths(temporary.Path);
        var database = new SqliteDatabase(paths);
        var repository = new FitnessRepository(database);
        await repository.InitializeAsync();
        var plan = await repository.GetCurrentPlanAsync();
        var planItem = plan.Days.Single(day => day.Code == "A").Items.OrderBy(item => item.Position).First();
        var option = planItem.Options.Single(item => item.IsPreferred);
        var userId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var setId = Guid.NewGuid();
        var readinessId = Guid.NewGuid();
        var cardioId = Guid.NewGuid();
        var assignmentId = Guid.NewGuid();
        var planDayId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 8, 9, 8, 0, 0, TimeSpan.Zero);

        var bootstrap = new BootstrapDto
        {
            User = new UserDto
            {
                Id = userId,
                Email = "cloud@example.com",
                DisplayName = "云端用户",
                Timezone = "Asia/Shanghai",
                WeightUnit = "KG",
                Version = 3,
                UpdatedAt = now
            },
            WorkoutSessions =
            [
                new WorkoutSessionDto
                {
                    Id = sessionId,
                    UserId = userId,
                    Source = "web",
                    SourceDevice = "web",
                    PlanAssignmentId = assignmentId,
                    PlanVersionId = plan.Id,
                    PlanDayId = planDayId,
                    PlanDayCode = "A",
                    LocalDate = new DateOnly(2026, 8, 9),
                    Timezone = "Asia/Shanghai",
                    StartedAt = now,
                    CompletedAt = now.AddHours(1),
                    Status = "COMPLETED",
                    PlanSnapshotJson = "{}",
                    Version = 4,
                    UpdatedAt = now.AddHours(1),
                    Sets =
                    [
                        new WorkoutSetDto
                        {
                            Id = setId,
                            SessionId = sessionId,
                            PlanSlotId = planItem.Id,
                            SourcePlanSlotOptionId = option.Id,
                            ExerciseId = option.ExerciseId,
                            EquipmentId = option.EquipmentId,
                            SetNumber = 1,
                            WeightKg = 40,
                            Reps = 10,
                            Rir = 2,
                            Completed = true,
                            CompletedAt = now.AddMinutes(10),
                            Version = 5,
                            UpdatedAt = now.AddMinutes(10)
                        }
                    ]
                }
            ],
            Readiness =
            [
                new ReadinessDto
                {
                    Id = readinessId,
                    UserId = userId,
                    LocalDate = new DateOnly(2026, 8, 9),
                    FatigueScore = 4,
                    SleepQuality = 5,
                    Version = 2,
                    UpdatedAt = now
                }
            ],
            CardioSessions =
            [
                new CardioSessionDto
                {
                    Id = cardioId,
                    UserId = userId,
                    Source = "web",
                    LocalDate = new DateOnly(2026, 8, 8),
                    Activity = "cycling",
                    DurationMinutes = 30,
                    DistanceKm = 12.5,
                    StartedAt = now.AddDays(-1),
                    CompletedAt = now.AddDays(-1).AddMinutes(30),
                    Version = 6,
                    UpdatedAt = now
                }
            ]
        };

        var applied = await repository.ApplyBootstrapAsync(ContractJson.Serialize(bootstrap));

        Assert.Equal(4, applied);
        var exported = Assert.Single(await repository.GetWorkoutExportSessionsAsync(), item => item.Id == sessionId);
        Assert.Equal("web", exported.Source);
        Assert.Equal(assignmentId, exported.PlanAssignmentId);
        Assert.Equal(planDayId, exported.PlanDayId);
        Assert.Equal("Asia/Shanghai", exported.Timezone);
        Assert.Equal(4, exported.ServerVersion);
        var exportedSet = Assert.Single(exported.Sets);
        Assert.Equal(option.EquipmentId, exportedSet.EquipmentId);
        Assert.Equal(5, exportedSet.ServerVersion);

        var updatedSet = bootstrap.WorkoutSessions[0].Sets[0] with
        {
            WeightKg = 42.5,
            Reps = 9,
            Version = 7,
            UpdatedAt = now.AddHours(2)
        };
        var updatedReadiness = bootstrap.Readiness[0] with
        {
            FatigueScore = 7,
            Version = 3,
            UpdatedAt = now.AddHours(2)
        };
        var updatedCardio = bootstrap.CardioSessions[0] with
        {
            DurationMinutes = 35,
            Version = 7,
            UpdatedAt = now.AddHours(2)
        };
        await repository.ApplyServerChangesAsync(
        [
            new SyncChange("workout_set", setId, "UPSERT", ContractJson.Serialize(updatedSet), now.AddHours(2), 7),
            new SyncChange("readiness", readinessId, "UPSERT", ContractJson.Serialize(updatedReadiness), now.AddHours(2), 3),
            new SyncChange("cardio_session", cardioId, "UPSERT", ContractJson.Serialize(updatedCardio), now.AddHours(2), 7)
        ]);

        exported = Assert.Single(await repository.GetWorkoutExportSessionsAsync(), item => item.Id == sessionId);
        Assert.Equal(42.5m, Assert.Single(exported.Sets).WeightKg);
        Assert.Equal(7, (await repository.GetLatestReadinessAsync())!.FatigueScore);

        var deletedAt = now.AddDays(1);
        static string Tombstone(Guid id, DateTimeOffset deletedAt) =>
            ContractJson.Serialize(new { id, deleted_at = deletedAt });
        await repository.ApplyServerChangesAsync(
        [
            new SyncChange("user", userId, "DELETE", Tombstone(userId, deletedAt), deletedAt, 4),
            new SyncChange("plan_version", plan.Id, "DELETE", Tombstone(plan.Id, deletedAt), deletedAt, 2),
            new SyncChange("workout_session", sessionId, "DELETE", Tombstone(sessionId, deletedAt), deletedAt, 8),
            new SyncChange("workout_set", setId, "DELETE", Tombstone(setId, deletedAt), deletedAt, 8),
            new SyncChange("daily_readiness", readinessId, "DELETE", Tombstone(readinessId, deletedAt), deletedAt, 4),
            new SyncChange("cardio", cardioId, "DELETE", Tombstone(cardioId, deletedAt), deletedAt, 8)
        ]);

        await using var connection = await database.OpenConnectionAsync();
        await using var verify = connection.CreateCommand();
        verify.CommandText = """
            SELECT
              (SELECT deleted_at IS NOT NULL FROM user_cache WHERE id=$userId),
              (SELECT deleted_at IS NOT NULL FROM plans WHERE id=$planId),
              (SELECT COUNT(*) FROM plan_assignments WHERE plan_version_id=$planId AND is_active=1),
              (SELECT deleted_at IS NOT NULL FROM workout_sessions WHERE id=$sessionId),
              (SELECT deleted_at IS NOT NULL FROM workout_sets WHERE id=$setId),
              (SELECT deleted_at IS NOT NULL FROM daily_readiness WHERE id=$readinessId),
              (SELECT deleted_at IS NOT NULL FROM cardio_sessions WHERE id=$cardioId),
              (SELECT source FROM cardio_sessions WHERE id=$cardioId);
            """;
        verify.Parameters.AddWithValue("$userId", userId.ToString("D"));
        verify.Parameters.AddWithValue("$planId", plan.Id.ToString("D"));
        verify.Parameters.AddWithValue("$sessionId", sessionId.ToString("D"));
        verify.Parameters.AddWithValue("$setId", setId.ToString("D"));
        verify.Parameters.AddWithValue("$readinessId", readinessId.ToString("D"));
        verify.Parameters.AddWithValue("$cardioId", cardioId.ToString("D"));
        await using var reader = await verify.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal(1L, reader.GetInt64(1));
        Assert.Equal(0L, reader.GetInt64(2));
        Assert.Equal(1L, reader.GetInt64(3));
        Assert.Equal(1L, reader.GetInt64(4));
        Assert.Equal(1L, reader.GetInt64(5));
        Assert.Equal(1L, reader.GetInt64(6));
        Assert.Equal("web", reader.GetString(7));
        Assert.DoesNotContain(await repository.GetPlanVersionsAsync(), item => item.Id == plan.Id);
    }
}
