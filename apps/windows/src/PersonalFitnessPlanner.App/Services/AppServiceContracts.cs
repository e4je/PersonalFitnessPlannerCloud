using System.Collections.ObjectModel;

namespace PersonalFitnessPlanner.App.Services;

public sealed record AppRuntimeOptions(string DataDirectory, bool Offline, bool SmokeTest);

public sealed record DashboardData(
    string PlanName,
    int PlanVersion,
    string Recommendation,
    string NextDay,
    int CompletedThisWeek,
    int WeeklyTarget,
    string SyncStatus,
    IReadOnlyList<WorkoutHistoryRow> RecentWorkouts);

public sealed record ExerciseOptionData(
    Guid Id,
    Guid ExerciseId,
    string ExerciseName,
    string Equipment,
    bool IsPreferred,
    int Sets,
    int RepMin,
    int RepMax,
    string RepUnit,
    int RestSeconds,
    Guid? EquipmentId = null);

public sealed record PlanItemData(
    Guid Id,
    int Position,
    string BodyPart,
    string Cues,
    string CommonMistakes,
    IReadOnlyList<ExerciseOptionData> Options,
    string SeatPosition = "",
    string BenchAngle = "",
    string MachineNumber = "");

public sealed record PlanDayData(string Code, string Name, IReadOnlyList<PlanItemData> Items);

public sealed record PlanData(
    Guid Id,
    Guid PlanId,
    string Name,
    int Version,
    string Status,
    int DeloadWeeks,
    int DeloadMaxSets,
    IReadOnlyList<PlanDayData> Days,
    DateTimeOffset? PublishedAt = null,
    int WeeklyStrengthTarget = 3,
    int MinimumRestDays = 1,
    int FatigueThreshold = 8);

public sealed record ActiveWorkoutData(
    Guid SessionId,
    string DayCode,
    DateOnly LocalDate,
    PlanData Snapshot,
    IReadOnlyList<SavedSetData> SavedSets,
    DateTimeOffset StartedAt);

public sealed record SavedSetData(
    Guid Id,
    Guid SessionId,
    Guid PlanItemId,
    Guid OptionId,
    int SetNumber,
    decimal? WeightKg,
    int? Reps,
    int? DurationSeconds,
    int? Rir,
    bool Pain,
    string Notes,
    DateTimeOffset CompletedAt);

public sealed record SaveSetRequest(
    Guid SessionId,
    Guid PlanItemId,
    ExerciseOptionData Option,
    int SetNumber,
    decimal? WeightKg,
    int? Reps,
    int? DurationSeconds,
    int? Rir,
    bool Pain,
    string Notes,
    string ClientSetKey);

public sealed record WorkoutHistoryRow(
    Guid Id,
    DateOnly LocalDate,
    string DayCode,
    string Source,
    string Status,
    int SetCount,
    decimal VolumeKg,
    string SyncStatus,
    string PlanVersion,
    string ExerciseNames,
    decimal PeakWeightKg = 0,
    int TotalReps = 0);

public sealed record ExerciseLibraryItem(
    Guid Id,
    string Name,
    string BodyPart,
    string Equipment,
    string Prescription,
    string Cues,
    string CommonMistakes,
    string Alternatives,
    long Version);

public sealed record AppSettingsData(
    string ApiBaseUrl,
    string TimeZone,
    string UnitSystem,
    string TrainingDays,
    string Theme,
    string DataDirectory,
    bool AutomaticSync,
    string Version);

public sealed record AuthenticationState(bool IsAuthenticated, bool IsAdmin, string DisplayName, string RoleSource);

public sealed record SyncResult(bool Success, int Uploaded, int Downloaded, string Message);

public sealed record DailyReadinessData(Guid Id, DateOnly LocalDate, int FatigueScore, int? SleepQuality, string PainNotes, string Notes);
public sealed record ExerciseSetupPreferenceData(Guid ExerciseId, string EquipmentKey, string SeatPosition, string BenchAngle, string MachineNumber, string Notes);
public sealed record WeightSuggestionData(decimal? LastWeightKg, int? LastReps, decimal SuggestedWeightKg, string Action, string Reason, bool PainReported);

public interface IAppDataService
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<DashboardData> GetDashboardAsync(CancellationToken cancellationToken = default);
    Task<PlanData> GetCurrentPlanAsync(CancellationToken cancellationToken = default);
    Task<ActiveWorkoutData?> GetActiveWorkoutAsync(CancellationToken cancellationToken = default);
    Task<ActiveWorkoutData> StartWorkoutAsync(string dayCode, CancellationToken cancellationToken = default);
    Task<bool> SaveSetAsync(SaveSetRequest request, CancellationToken cancellationToken = default);
    Task<SavedSetData?> UpdatePreviousSetAsync(Guid sessionId, Guid planItemId, decimal? weightKg, int? reps, int? rir, bool pain, CancellationToken cancellationToken = default);
    Task CompleteWorkoutAsync(Guid sessionId, bool endedEarly, CancellationToken cancellationToken = default);
    Task MarkTodayAsync(string kind, CancellationToken cancellationToken = default);
    Task SaveReadinessAsync(DailyReadinessData readiness, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WorkoutHistoryRow>> GetHistoryAsync(DateOnly? from, DateOnly? to, string? dayCode, string? exercise, CancellationToken cancellationToken = default);
    Task<SavedSetData?> UpdateHistoricalLastSetAsync(Guid sessionId, decimal? weightKg, int? reps, int? rir, bool pain, CancellationToken cancellationToken = default);
    Task SoftDeleteWorkoutAsync(Guid sessionId, CancellationToken cancellationToken = default);
    Task<string> ExportHistoryCsvAsync(string targetDirectory, CancellationToken cancellationToken = default);
    Task<string> ExportDataJsonAsync(string targetDirectory, CancellationToken cancellationToken = default);
    Task ImportDataJsonAsync(string filePath, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ExerciseLibraryItem>> GetExercisesAsync(CancellationToken cancellationToken = default);
    Task SaveExerciseDraftAsync(ExerciseLibraryItem exercise, CancellationToken cancellationToken = default);
    Task PublishExerciseAsync(Guid exerciseId, CancellationToken cancellationToken = default);
    Task<PlanData> CreatePlanDraftAsync(CancellationToken cancellationToken = default);
    Task SavePlanDraftAsync(PlanData plan, CancellationToken cancellationToken = default);
    Task<PlanData> PublishPlanAsync(PlanData plan, CancellationToken cancellationToken = default);
    Task AssignPlanAsync(Guid planVersionId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PlanData>> GetPlanVersionsAsync(CancellationToken cancellationToken = default);
    Task RollbackAssignmentAsync(Guid planVersionId, CancellationToken cancellationToken = default);
    Task<AppSettingsData> GetSettingsAsync(CancellationToken cancellationToken = default);
    Task SaveSettingsAsync(AppSettingsData settings, CancellationToken cancellationToken = default);
    Task<AuthenticationState> LoginAsync(string email, string password, CancellationToken cancellationToken = default);
    Task LogoutAsync(CancellationToken cancellationToken = default);
    Task<AuthenticationState> GetAuthenticationStateAsync(CancellationToken cancellationToken = default);
    Task<SyncResult> SynchronizeAsync(CancellationToken cancellationToken = default);
    Task<SyncResult> FullResynchronizeAsync(CancellationToken cancellationToken = default);
    Task<SyncResult> UploadLocalAsync(CancellationToken cancellationToken = default);
    Task<SyncResult> DownloadCloudOverwriteAsync(CancellationToken cancellationToken = default);
    Task SaveExerciseSetupPreferenceAsync(ExerciseSetupPreferenceData preference, CancellationToken cancellationToken = default);
    Task<ExerciseSetupPreferenceData?> GetExerciseSetupPreferenceAsync(Guid exerciseId, string equipmentKey, CancellationToken cancellationToken = default);
    Task<WeightSuggestionData> GetWeightSuggestionAsync(ExerciseOptionData option, decimal minimumIncrementKg = 2.5m, CancellationToken cancellationToken = default);
    Task<string> CreateBackupAsync(CancellationToken cancellationToken = default);
    void LogError(Exception exception, string context);
}
