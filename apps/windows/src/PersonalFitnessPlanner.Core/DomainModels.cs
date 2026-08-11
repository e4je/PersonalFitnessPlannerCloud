namespace PersonalFitnessPlanner.Core;

public enum PlanStatus
{
    Draft,
    Published,
    Archived,
}

public enum PlanDayCode
{
    A,
    B,
}

public enum WorkoutStatus
{
    InProgress,
    Completed,
    EndedEarly,
    Deleted,
}

public enum SetQuality
{
    Poor,
    Fair,
    Good,
}

public enum UnitSystem
{
    Kilograms,
    Pounds,
}

public sealed record Equipment(
    Guid Id,
    string Name,
    string Category,
    string? Brand = null,
    string? Model = null,
    string? Notes = null,
    long EntityVersion = 1,
    DateTimeOffset? CreatedAt = null,
    DateTimeOffset? UpdatedAt = null,
    DateTimeOffset? DeletedAt = null);

public sealed record ExerciseAlternative(
    Guid Id,
    Guid ExerciseId,
    Guid AlternativeExerciseId,
    int SortOrder = 0);

public sealed record ExerciseDefinition(
    Guid Id,
    string Name,
    string BodyPart,
    Guid? EquipmentId,
    int DefaultSets,
    int RepMin,
    int RepMax,
    string Cues,
    string CommonMistakes,
    IReadOnlyList<ExerciseAlternative> Alternatives,
    string RepUnit = "reps",
    int DefinitionVersion = 1,
    long EntityVersion = 1,
    DateTimeOffset? CreatedAt = null,
    DateTimeOffset? UpdatedAt = null,
    DateTimeOffset? DeletedAt = null);

/// <summary>A preferred exercise or one valid substitute for a single plan position.</summary>
public sealed record ExerciseOption(
    Guid Id,
    Guid ExerciseId,
    string ExerciseName,
    string EquipmentName,
    bool IsPreferred,
    int SetCount,
    int RepMin,
    int RepMax,
    Guid? EquipmentId = null,
    int SortOrder = 0,
    string RepUnit = "reps",
    int RirMin = 2,
    int RirMax = 3,
    int RestSeconds = 90,
    int IntroSetCount = 2,
    int IntroWeeks = 2)
{
    public string Equipment => EquipmentName;

    public int Sets => SetCount;
}

/// <summary>A plan position. Exactly one of its options is executed in a workout.</summary>
public sealed record PlanItem(
    Guid Id,
    int Position,
    string BodyPart,
    string Cues,
    IReadOnlyList<ExerciseOption> Options,
    string CommonMistakes = "",
    string SeatPosition = "",
    string BenchAngle = "",
    string MachineNumber = "");

public sealed record PlanDay(
    Guid Id,
    string Code,
    string Name,
    IReadOnlyList<PlanItem> Items,
    int SortOrder = 0)
{
    public PlanDayCode? ParsedCode =>
        Enum.TryParse<PlanDayCode>(Code, ignoreCase: true, out var code) ? code : null;
}

/// <summary>
/// Immutable plan-version snapshot. A published instance must never be edited;
/// <see cref="PlanVersionPolicy"/> creates a new draft with new nested UUIDs.
/// </summary>
public sealed record TrainingPlan(
    Guid Id,
    Guid PlanId,
    string Name,
    int VersionNumber,
    PlanStatus Status,
    IReadOnlyList<PlanDay> Days,
    int IntroWeeks = 2,
    int IntroMaxSets = 2,
    string Description = "",
    DateTimeOffset? PublishedAt = null,
    string? SnapshotJson = null,
    long EntityVersion = 1,
    DateTimeOffset? CreatedAt = null,
    DateTimeOffset? UpdatedAt = null,
    DateTimeOffset? DeletedAt = null)
{
    public int Version => VersionNumber;

    public int DeloadWeeks => IntroWeeks;

    public int DeloadMaxSets => IntroMaxSets;

    public static TrainingPlan NewDraft(
        string name,
        IReadOnlyList<PlanDay>? days = null,
        Guid? planId = null,
        Guid? versionId = null,
        DateTimeOffset? now = null) =>
        new(
            versionId ?? Guid.NewGuid(),
            planId ?? Guid.NewGuid(),
            name,
            1,
            PlanStatus.Draft,
            days ?? Array.Empty<PlanDay>(),
            CreatedAt: now ?? DateTimeOffset.UtcNow,
            UpdatedAt: now ?? DateTimeOffset.UtcNow);
}

public sealed record PlanAssignment(
    Guid Id,
    Guid UserId,
    Guid PlanVersionId,
    DateOnly StartLocalDate,
    DateOnly? EndLocalDate = null,
    bool IsActive = true,
    long EntityVersion = 1,
    DateTimeOffset? CreatedAt = null,
    DateTimeOffset? UpdatedAt = null,
    DateTimeOffset? DeletedAt = null);

public sealed record WorkoutSet(
    Guid Id,
    Guid SessionId,
    Guid ExerciseId,
    int SetNumber,
    double? WeightKg,
    int? Reps,
    Guid? PlanItemId = null,
    Guid? SourceExerciseOptionId = null,
    Guid? EquipmentId = null,
    int? DurationSeconds = null,
    bool IsWarmup = false,
    int? Rir = null,
    SetQuality? Quality = null,
    bool Pain = false,
    string? Notes = null,
    bool Completed = false,
    DateTimeOffset? CompletedAt = null,
    long EntityVersion = 1,
    DateTimeOffset? CreatedAt = null,
    DateTimeOffset? UpdatedAt = null,
    DateTimeOffset? DeletedAt = null)
{
    public double VolumeKg => TrainingVolumeCalculator.CalculateSetVolume(WeightKg, Reps);
}

/// <summary>
/// A workout owns the exact serialized plan snapshot used at start time, so
/// history remains reproducible after later plan versions are published.
/// </summary>
public sealed record WorkoutSession(
    Guid Id,
    Guid UserId,
    DateOnly LocalDate,
    string TimeZoneId,
    DateTimeOffset StartedAt,
    WorkoutStatus Status,
    IReadOnlyList<WorkoutSet> Sets,
    Guid? PlanVersionId = null,
    string? PlanDayCode = null,
    DateTimeOffset? CompletedAt = null,
    bool IsFullBody = true,
    string PlanSnapshotJson = "{}",
    string IdempotencyKey = "",
    string? Notes = null,
    string Source = "windows",
    long EntityVersion = 1,
    DateTimeOffset? CreatedAt = null,
    DateTimeOffset? UpdatedAt = null,
    DateTimeOffset? DeletedAt = null)
{
    public double VolumeKg => TrainingVolumeCalculator.CalculateSessionVolume(Sets);
}

public sealed record Readiness(
    Guid Id,
    Guid UserId,
    DateOnly LocalDate,
    int FatigueScore,
    int? SleepQuality = null,
    string? PainNotes = null,
    string? Notes = null,
    long EntityVersion = 1,
    DateTimeOffset? CreatedAt = null,
    DateTimeOffset? UpdatedAt = null,
    DateTimeOffset? DeletedAt = null);

public sealed record CardioSession(
    Guid Id,
    Guid UserId,
    DateOnly LocalDate,
    string Activity,
    int DurationMinutes,
    DateTimeOffset StartedAt,
    double? DistanceKm = null,
    string? Notes = null,
    DateTimeOffset? CompletedAt = null,
    long EntityVersion = 1,
    DateTimeOffset? CreatedAt = null,
    DateTimeOffset? UpdatedAt = null,
    DateTimeOffset? DeletedAt = null);
