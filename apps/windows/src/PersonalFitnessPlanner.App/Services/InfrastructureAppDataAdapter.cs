using System.IO;
using Infra = PersonalFitnessPlanner.Infrastructure;
using InfraModels = PersonalFitnessPlanner.Infrastructure.Models;

namespace PersonalFitnessPlanner.App.Services;

public sealed class InfrastructureAppDataAdapter : IAppDataService, IDisposable
{
    private readonly Infra.AppDataService _inner;
    private readonly AppRuntimeOptions _runtime;

    public InfrastructureAppDataAdapter(AppRuntimeOptions runtime)
    {
        _runtime = runtime;
        _inner = new Infra.AppDataService(new Infra.AppPaths(runtime.DataDirectory));
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default) => _inner.InitializeAsync(cancellationToken);

    public async Task<DashboardData> GetDashboardAsync(CancellationToken cancellationToken = default)
    {
        var value = await _inner.GetDashboardAsync(cancellationToken);
        return new DashboardData(value.PlanName, value.PlanVersion, value.Recommendation, value.NextDay,
            value.CompletedThisWeek, value.WeeklyTarget, value.SyncStatus, value.RecentWorkouts.Select(Map).ToArray());
    }

    public async Task<PlanData> GetCurrentPlanAsync(CancellationToken cancellationToken = default) =>
        Map(await _inner.GetCurrentPlanAsync(cancellationToken));

    public async Task<ActiveWorkoutData?> GetActiveWorkoutAsync(CancellationToken cancellationToken = default)
    {
        var value = await _inner.GetActiveWorkoutAsync(cancellationToken);
        return value is null ? null : Map(value);
    }

    public async Task<ActiveWorkoutData> StartWorkoutAsync(string dayCode, CancellationToken cancellationToken = default) =>
        Map(await _inner.StartWorkoutAsync(dayCode, cancellationToken));

    public Task<bool> SaveSetAsync(SaveSetRequest request, CancellationToken cancellationToken = default) =>
        _inner.SaveSetAsync(new InfraModels.SaveSetInput(
            request.SessionId, request.PlanItemId, Map(request.Option), request.SetNumber, request.WeightKg,
            request.Reps, request.DurationSeconds, request.Rir, request.Pain, request.Notes, request.ClientSetKey), cancellationToken);

    public async Task<SavedSetData?> UpdatePreviousSetAsync(Guid sessionId, Guid planItemId, decimal? weightKg, int? reps, int? rir, bool pain, CancellationToken cancellationToken = default)
    {
        var value = await _inner.UpdatePreviousSetAsync(sessionId, planItemId, weightKg, reps, rir, pain, cancellationToken);
        return value is null ? null : Map(value);
    }

    public async Task<SavedSetData?> UpdateHistoricalLastSetAsync(Guid sessionId, decimal? weightKg, int? reps, int? rir, bool pain, CancellationToken cancellationToken = default)
    {
        var value = await _inner.UpdateHistoricalLastSetAsync(sessionId, weightKg, reps, rir, pain, cancellationToken);
        return value is null ? null : Map(value);
    }

    public Task CompleteWorkoutAsync(Guid sessionId, bool endedEarly, CancellationToken cancellationToken = default) =>
        _inner.CompleteWorkoutAsync(sessionId, endedEarly, cancellationToken);

    public Task MarkTodayAsync(string kind, CancellationToken cancellationToken = default) =>
        _inner.MarkTodayAsync(kind, cancellationToken);

    public Task SaveReadinessAsync(DailyReadinessData readiness, CancellationToken cancellationToken = default) =>
        _inner.SaveReadinessAsync(new InfraModels.DailyReadinessData(
            readiness.Id, readiness.LocalDate, readiness.FatigueScore, readiness.SleepQuality, readiness.PainNotes, readiness.Notes), cancellationToken);

    public async Task<IReadOnlyList<WorkoutHistoryRow>> GetHistoryAsync(DateOnly? from, DateOnly? to, string? dayCode, string? exercise, CancellationToken cancellationToken = default) =>
        (await _inner.GetHistoryAsync(from, to, dayCode, exercise, cancellationToken)).Select(Map).ToArray();

    public Task SoftDeleteWorkoutAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        _inner.SoftDeleteWorkoutAsync(sessionId, cancellationToken);

    public Task<string> ExportHistoryCsvAsync(string targetDirectory, CancellationToken cancellationToken = default) =>
        _inner.ExportHistoryCsvAsync(targetDirectory, cancellationToken);

    public Task<string> ExportDataJsonAsync(string targetDirectory, CancellationToken cancellationToken = default) =>
        _inner.ExportDataJsonAsync(targetDirectory, cancellationToken);

    public Task ImportDataJsonAsync(string filePath, CancellationToken cancellationToken = default) =>
        _inner.ImportDataJsonAsync(filePath, cancellationToken);

    public async Task<IReadOnlyList<ExerciseLibraryItem>> GetExercisesAsync(CancellationToken cancellationToken = default) =>
        (await _inner.GetExercisesAsync(cancellationToken)).Select(Map).ToArray();

    public Task SaveExerciseDraftAsync(ExerciseLibraryItem exercise, CancellationToken cancellationToken = default) =>
        _inner.SaveExerciseDraftAsync(new InfraModels.ExerciseLibraryItem(
            exercise.Id, exercise.Name, exercise.BodyPart, exercise.Equipment, exercise.Prescription,
            exercise.Cues, exercise.CommonMistakes, exercise.Alternatives, exercise.Version, "draft"), cancellationToken);

    public Task PublishExerciseAsync(Guid exerciseId, CancellationToken cancellationToken = default) =>
        _inner.PublishExerciseAsync(exerciseId, cancellationToken);

    public async Task<PlanData> CreatePlanDraftAsync(CancellationToken cancellationToken = default) =>
        Map(await _inner.CreatePlanDraftAsync(cancellationToken));

    public Task SavePlanDraftAsync(PlanData plan, CancellationToken cancellationToken = default) =>
        _inner.SavePlanDraftAsync(Map(plan), cancellationToken);

    public async Task<PlanData> PublishPlanAsync(PlanData plan, CancellationToken cancellationToken = default) =>
        Map(await _inner.PublishPlanAsync(Map(plan), cancellationToken));

    public Task AssignPlanAsync(Guid planVersionId, CancellationToken cancellationToken = default) =>
        _inner.AssignPlanAsync(planVersionId, cancellationToken);

    public async Task<IReadOnlyList<PlanData>> GetPlanVersionsAsync(CancellationToken cancellationToken = default) =>
        (await _inner.GetPlanVersionsAsync(cancellationToken)).Select(Map).ToArray();

    public Task RollbackAssignmentAsync(Guid planVersionId, CancellationToken cancellationToken = default) =>
        _inner.RollbackAssignmentAsync(planVersionId, cancellationToken);

    public async Task<AppSettingsData> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        var value = await _inner.GetSettingsAsync(cancellationToken);
        return new AppSettingsData(value.ApiBaseUrl, value.TimeZone, value.UnitSystem.ToUpperInvariant(),
            value.TrainingDays, NormalizeTheme(value.Theme), value.DataDirectory, value.AutomaticSync, value.Version);
    }

    public async Task SaveSettingsAsync(AppSettingsData settings, CancellationToken cancellationToken = default)
    {
        var requestedDirectory = Path.GetFullPath(settings.DataDirectory);
        var currentDirectory = Path.GetFullPath(_inner.Paths.DataDirectory);
        var mapped = new InfraModels.AppSettingsData(settings.ApiBaseUrl, settings.TimeZone,
            settings.UnitSystem.ToLowerInvariant(), settings.TrainingDays, settings.Theme.ToLowerInvariant(),
            requestedDirectory, settings.AutomaticSync, settings.Version);

        if (!string.Equals(requestedDirectory, currentDirectory, StringComparison.OrdinalIgnoreCase))
        {
            await MigrateDataDirectoryAsync(requestedDirectory, mapped, cancellationToken);
        }
        await _inner.SaveSettingsAsync(mapped, cancellationToken);
    }

    public async Task<AuthenticationState> LoginAsync(string email, string password, CancellationToken cancellationToken = default) =>
        Map(await _inner.LoginAsync(email, password, cancellationToken));

    public Task LogoutAsync(CancellationToken cancellationToken = default) => _inner.LogoutAsync(cancellationToken);

    public async Task<AuthenticationState> GetAuthenticationStateAsync(CancellationToken cancellationToken = default) =>
        Map(await _inner.GetAuthenticationStateAsync(cancellationToken));

    public async Task<SyncResult> SynchronizeAsync(CancellationToken cancellationToken = default)
    {
        if (_runtime.Offline) return new SyncResult(false, 0, 0, "离线模式：记录已安全保存在 Outbox");
        return Map(await _inner.SynchronizeAsync(cancellationToken));
    }

    public async Task<SyncResult> FullResynchronizeAsync(CancellationToken cancellationToken = default)
    {
        if (_runtime.Offline) return new SyncResult(false, 0, 0, "离线模式不能执行完整重新同步");
        return Map(await _inner.FullResynchronizeAsync(cancellationToken));
    }

    public Task SaveExerciseSetupPreferenceAsync(ExerciseSetupPreferenceData preference, CancellationToken cancellationToken = default) =>
        _inner.SaveExerciseSetupPreferenceAsync(new InfraModels.ExerciseSetupPreferenceData(
            preference.ExerciseId, preference.EquipmentKey, preference.SeatPosition, preference.BenchAngle,
            preference.MachineNumber, preference.Notes), cancellationToken);

    public async Task<ExerciseSetupPreferenceData?> GetExerciseSetupPreferenceAsync(Guid exerciseId, string equipmentKey, CancellationToken cancellationToken = default)
    {
        var value = await _inner.GetExerciseSetupPreferenceAsync(exerciseId, equipmentKey, cancellationToken);
        return value is null ? null : new ExerciseSetupPreferenceData(value.ExerciseId, value.EquipmentKey,
            value.SeatPosition, value.BenchAngle, value.MachineNumber, value.Notes);
    }

    public async Task<WeightSuggestionData> GetWeightSuggestionAsync(ExerciseOptionData option, decimal minimumIncrementKg = 2.5m, CancellationToken cancellationToken = default)
    {
        var value = await _inner.GetWeightSuggestionAsync(Map(option), minimumIncrementKg, cancellationToken);
        return new WeightSuggestionData(value.LastWeightKg, value.LastReps, value.SuggestedWeightKg,
            value.Action, value.Reason, value.PainReported);
    }

    public Task<string> CreateBackupAsync(CancellationToken cancellationToken = default) =>
        _inner.CreateBackupAsync(cancellationToken);

    public void LogError(Exception exception, string context) => _inner.LogError(exception, context);

    public void Dispose() => _inner.Dispose();

    private async Task MigrateDataDirectoryAsync(string targetDirectory, InfraModels.AppSettingsData settings, CancellationToken cancellationToken)
    {
        var root = Path.GetPathRoot(targetDirectory);
        if (string.IsNullOrWhiteSpace(targetDirectory) || string.Equals(targetDirectory.TrimEnd(Path.DirectorySeparatorChar), root?.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("数据目录不能是磁盘根目录。");

        var targetPaths = new Infra.AppPaths(targetDirectory);
        targetPaths.EnsureCreated();
        if (File.Exists(targetPaths.DatabasePath))
            throw new IOException($"目标数据目录已存在 fitness.db，为避免覆盖请先选择空目录：{targetDirectory}");

        var snapshot = await _inner.CreateBackupAsync(cancellationToken);
        File.Copy(snapshot, targetPaths.DatabasePath, overwrite: false);
        await new Infra.SettingsStore(targetPaths).SaveAsync(settings, cancellationToken);
        if (File.Exists(_inner.Paths.TokenPath)) File.Copy(_inner.Paths.TokenPath, targetPaths.TokenPath, overwrite: true);
        DataDirectoryPointer.Save(targetDirectory);
    }

    private static DashboardData Map(InfraModels.DashboardData value) => new(
        value.PlanName, value.PlanVersion, value.Recommendation, value.NextDay, value.CompletedThisWeek,
        value.WeeklyTarget, value.SyncStatus, value.RecentWorkouts.Select(Map).ToArray());

    private static WorkoutHistoryRow Map(InfraModels.WorkoutHistoryRow value) => new(
        value.Id, value.LocalDate, value.DayCode, value.Source, value.Status, value.SetCount, value.VolumeKg,
        value.SyncStatus, value.PlanVersion, value.ExerciseNames, value.PeakWeightKg, value.TotalReps);

    private static ExerciseLibraryItem Map(InfraModels.ExerciseLibraryItem value) => new(
        value.Id, value.Name, value.BodyPart, value.Equipment, value.Prescription, value.Cues,
        value.CommonMistakes, value.Alternatives, value.Version);

    private static AuthenticationState Map(InfraModels.AuthenticationState value) =>
        new(value.IsAuthenticated, value.IsAdmin, value.DisplayName, value.RoleSource);

    private static SyncResult Map(InfraModels.SyncResult value) =>
        new(value.Success, value.Uploaded, value.Downloaded, value.Message);

    private static ActiveWorkoutData Map(InfraModels.ActiveWorkoutData value) => new(
        value.SessionId, value.DayCode, value.LocalDate, Map(value.Snapshot), value.SavedSets.Select(Map).ToArray(), value.StartedAt);

    private static SavedSetData Map(InfraModels.SavedSetData value) => new(
        value.Id, value.SessionId, value.PlanItemId, value.OptionId, value.SetNumber, value.WeightKg,
        value.Reps, value.DurationSeconds, value.Rir, value.Pain, value.Notes, value.CompletedAt);

    private static PlanData Map(InfraModels.PlanData value) => new(
        value.Id, value.PlanId, value.Name, value.Version, value.Status, value.DeloadWeeks, value.DeloadMaxSets,
        value.Days.Select(x => new PlanDayData(x.Code, x.Name, x.Items.Select(y => new PlanItemData(
            y.Id, y.Position, y.BodyPart, y.Cues, y.CommonMistakes, y.Options.Select(Map).ToArray(),
            y.SeatPosition, y.BenchAngle, y.MachineNumber)).ToArray())).ToArray(), value.PublishedAt,
        value.WeeklyStrengthTarget, value.MinimumRestDays, value.FatigueThreshold);

    private static ExerciseOptionData Map(InfraModels.ExerciseOptionData value) => new(
        value.Id, value.ExerciseId, value.ExerciseName, value.Equipment, value.IsPreferred, value.Sets,
        value.RepMin, value.RepMax, value.RepUnit, value.RestSeconds, value.EquipmentId);

    private static InfraModels.PlanData Map(PlanData value) => new(
        value.Id, value.PlanId, value.Name, value.Version, value.Status.ToLowerInvariant(), value.DeloadWeeks,
        value.DeloadMaxSets, value.Days.Select(x => new InfraModels.PlanDayData(x.Code, x.Name,
            x.Items.Select(y => new InfraModels.PlanItemData(y.Id, y.Position, y.BodyPart, y.Cues,
                y.CommonMistakes, y.Options.Select(Map).ToArray(), y.SeatPosition, y.BenchAngle, y.MachineNumber)).ToArray())).ToArray(),
        value.PublishedAt, value.WeeklyStrengthTarget, value.MinimumRestDays, value.FatigueThreshold);

    private static InfraModels.ExerciseOptionData Map(ExerciseOptionData value) => new(
        value.Id, value.ExerciseId, value.ExerciseName, value.Equipment, value.IsPreferred, value.Sets,
        value.RepMin, value.RepMax, value.RepUnit, value.RestSeconds, value.EquipmentId);

    private static string NormalizeTheme(string value) => value.ToLowerInvariant() switch
    {
        "dark" => "Dark",
        "light" => "Light",
        _ => "System"
    };
}
