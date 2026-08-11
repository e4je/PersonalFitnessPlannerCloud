namespace PersonalFitnessPlanner.Contracts;

public sealed record WorkoutSetDto : CloudEntityDto
{
    public Guid SessionId { get; init; }

    public Guid? PlanSlotId { get; init; }

    public Guid? SourcePlanSlotOptionId { get; init; }

    public Guid ExerciseId { get; init; }

    public Guid? EquipmentId { get; init; }

    public int SetNumber { get; init; }

    public double? WeightKg { get; init; }

    public int? Reps { get; init; }

    public int? DurationSeconds { get; init; }

    public bool IsWarmup { get; init; }

    public int? Rir { get; init; }

    public string? Quality { get; init; }

    public bool Pain { get; init; }

    public string? Notes { get; init; }

    public bool Completed { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }
}

public sealed record WorkoutSessionDto : CloudEntityDto
{
    public Guid UserId { get; init; }

    public Guid? ClientId { get; init; }

    public string Source { get; init; } = string.Empty;

    public string? SourceDevice { get; init; }

    public string? ClientVersion { get; init; }

    public Guid? PlanAssignmentId { get; init; }

    public Guid? PlanVersionId { get; init; }

    public Guid? PlanDayId { get; init; }

    public string? PlanDayCode { get; init; }

    public DateOnly LocalDate { get; init; }

    public string Timezone { get; init; } = "UTC";

    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public string Status { get; init; } = "IN_PROGRESS";

    public bool IsFullBody { get; init; } = true;

    public string PlanSnapshotJson { get; init; } = "{}";

    public string? IdempotencyKey { get; init; }

    public string? Notes { get; init; }

    public IReadOnlyList<WorkoutSetDto> Sets { get; init; } = Array.Empty<WorkoutSetDto>();
}

public sealed record WorkoutSetUpsertDto
{
    public Guid Id { get; init; }

    public Guid? PlanSlotId { get; init; }

    public Guid? SourcePlanSlotOptionId { get; init; }

    public Guid ExerciseId { get; init; }

    public Guid? EquipmentId { get; init; }

    public int SetNumber { get; init; }

    public double? WeightKg { get; init; }

    public int? Reps { get; init; }

    public int? DurationSeconds { get; init; }

    public bool IsWarmup { get; init; }

    public int? Rir { get; init; }

    public string? Quality { get; init; }

    public bool Pain { get; init; }

    public string? Notes { get; init; }

    public bool Completed { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public DateTimeOffset? DeletedAt { get; init; }
}

public sealed record WorkoutSessionUpsertDto
{
    public Guid Id { get; init; }

    public Guid? PlanVersionId { get; init; }

    public string? PlanDayCode { get; init; }

    public DateOnly LocalDate { get; init; }

    public string Timezone { get; init; } = "UTC";

    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public string Status { get; init; } = "IN_PROGRESS";

    public bool IsFullBody { get; init; } = true;

    public string PlanSnapshotJson { get; init; } = "{}";

    public string? Notes { get; init; }

    public IReadOnlyList<WorkoutSetUpsertDto> Sets { get; init; }
        = Array.Empty<WorkoutSetUpsertDto>();

    public DateTimeOffset? DeletedAt { get; init; }
}

public sealed record ReadinessDto : CloudEntityDto
{
    public Guid UserId { get; init; }

    public DateOnly LocalDate { get; init; }

    public int FatigueScore { get; init; }

    public int? SleepQuality { get; init; }

    public string? PainNotes { get; init; }

    public string? Notes { get; init; }
}

public sealed record ReadinessUpsertDto
{
    public Guid Id { get; init; }

    public DateOnly LocalDate { get; init; }

    public int FatigueScore { get; init; }

    public int? SleepQuality { get; init; }

    public string? PainNotes { get; init; }

    public string? Notes { get; init; }
}

public sealed record CardioSessionDto : CloudEntityDto
{
    public Guid UserId { get; init; }

    public Guid? ClientId { get; init; }

    public string Source { get; init; } = string.Empty;

    public string? SourceDevice { get; init; }

    public string? ClientVersion { get; init; }

    public DateOnly LocalDate { get; init; }

    public string Activity { get; init; } = string.Empty;

    public string? ActivityType { get; init; }

    public int DurationMinutes { get; init; }

    public int? DurationSeconds { get; init; }

    public double? DistanceKm { get; init; }

    public double? DistanceMeters { get; init; }

    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public string? Notes { get; init; }
}

public sealed record WorkoutSessionPageDto : CursorPageDto
{
    public IReadOnlyList<WorkoutSessionDto> Items { get; init; }
        = Array.Empty<WorkoutSessionDto>();
}
