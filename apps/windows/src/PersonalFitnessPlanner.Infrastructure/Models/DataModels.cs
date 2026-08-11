namespace PersonalFitnessPlanner.Infrastructure.Models;

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
    DateTimeOffset CompletedAt,
    Guid ExerciseId = default,
    string Equipment = "",
    bool IsWarmup = false,
    Guid? EquipmentId = null,
    long ServerVersion = 0,
    DateTimeOffset? DeletedAt = null);

public sealed record SaveSetInput(
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

public sealed record ActiveWorkoutData(
    Guid SessionId,
    string DayCode,
    DateOnly LocalDate,
    PlanData Snapshot,
    IReadOnlyList<SavedSetData> SavedSets,
    DateTimeOffset StartedAt);

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

public sealed record DashboardData(
    string PlanName,
    int PlanVersion,
    string Recommendation,
    string NextDay,
    int CompletedThisWeek,
    int WeeklyTarget,
    string SyncStatus,
    IReadOnlyList<WorkoutHistoryRow> RecentWorkouts,
    string UnitSystem = "kg");

public sealed record ExerciseLibraryItem(
    Guid Id,
    string Name,
    string BodyPart,
    string Equipment,
    string Prescription,
    string Cues,
    string CommonMistakes,
    string Alternatives,
    long Version,
    string Status = "published");

public sealed record AppSettingsData(
    string ApiBaseUrl,
    string TimeZone,
    string UnitSystem,
    string TrainingDays,
    string Theme,
    string DataDirectory,
    bool AutomaticSync,
    string Version)
{
    public static AppSettingsData Default(string dataDirectory) => new(
        "https://localhost:8000/",
        "Asia/Shanghai",
        "kg",
        "1,3,5",
        "system",
        dataDirectory,
        true,
        typeof(AppSettingsData).Assembly.GetName().Version?.ToString(3) ?? "1.0.0");
}

public sealed record AuthenticationState(
    bool IsAuthenticated,
    bool IsAdmin,
    string DisplayName,
    string RoleSource);

public sealed record SyncResult(bool Success, int Uploaded, int Downloaded, string Message);

public sealed record OutboxStatusData(
    int Pending,
    int Failed,
    DateTimeOffset? LastSuccessfulSync,
    string Cursor,
    string LastError = "");

public sealed record StoredTokens(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset? ExpiresAt,
    string DisplayName = "");

public sealed record OutboxItem(
    Guid Id,
    string EntityType,
    Guid EntityId,
    string Operation,
    string IdempotencyKey,
    string PayloadJson,
    int AttemptCount,
    DateTimeOffset CreatedAt);

public sealed record SyncChange(
    string EntityType,
    Guid EntityId,
    string Operation,
    string PayloadJson,
    DateTimeOffset UpdatedAt,
    long Version = 0);

public sealed record SyncBatchFailure(
    Guid OutboxId,
    string Status,
    string Error,
    string ServerCopyJson,
    long? ServerVersion = null);

public sealed record SyncChangesPage(
    IReadOnlyList<SyncChange> Changes,
    string Cursor,
    bool HasMore = false,
    bool FullResyncRequired = false);

public sealed record DailyReadinessData(
    Guid Id,
    DateOnly LocalDate,
    int FatigueScore,
    int? SleepQuality,
    string PainNotes,
    string Notes);

public sealed record CardioSessionData(
    Guid Id,
    DateOnly LocalDate,
    string Activity,
    int DurationMinutes,
    decimal? DistanceKm,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    string Notes);

public sealed record ExerciseSetupPreferenceData(
    Guid ExerciseId,
    string EquipmentKey,
    string SeatPosition,
    string BenchAngle,
    string MachineNumber,
    string Notes);

public sealed record ExerciseSetHistoryData(
    Guid SessionId,
    Guid ExerciseId,
    Guid OptionId,
    string Equipment,
    decimal? WeightKg,
    int? Reps,
    int? Rir,
    bool Pain,
    DateTimeOffset CompletedAt);

public sealed record WeightSuggestionData(
    decimal? LastWeightKg,
    int? LastReps,
    decimal SuggestedWeightKg,
    string Action,
    string Reason,
    bool PainReported);

public sealed record HistoryExport(
    int SchemaVersion,
    DateTimeOffset ExportedAt,
    IReadOnlyList<PlanData> Plans,
    IReadOnlyList<WorkoutExportSession> WorkoutSessions,
    AppSettingsData Settings);

public sealed record WorkoutExportSession(
    Guid Id,
    string DayCode,
    DateOnly LocalDate,
    string Status,
    string Source,
    Guid PlanVersionId,
    PlanData PlanSnapshot,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    bool EndedEarly,
    DateTimeOffset? DeletedAt,
    IReadOnlyList<SavedSetData> Sets,
    Guid? PlanAssignmentId = null,
    Guid? PlanDayId = null,
    string Timezone = "UTC",
    long ServerVersion = 0);
