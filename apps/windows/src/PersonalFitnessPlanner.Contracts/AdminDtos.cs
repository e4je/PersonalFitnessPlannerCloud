using System.Text.Json;

namespace PersonalFitnessPlanner.Contracts;

public sealed record ExerciseUpsertDto
{
    public string Name { get; init; } = string.Empty;

    public string BodyPart { get; init; } = string.Empty;

    public Guid? EquipmentId { get; init; }

    public int DefaultSets { get; init; }

    public int RepMin { get; init; }

    public int RepMax { get; init; }

    public string RepUnit { get; init; } = "reps";

    public string Cues { get; init; } = string.Empty;

    public string CommonMistakes { get; init; } = string.Empty;

    public IReadOnlyList<Guid> AlternativeExerciseIds { get; init; } = Array.Empty<Guid>();
}

public sealed record EquipmentUpsertDto
{
    public string Name { get; init; } = string.Empty;

    public string Category { get; init; } = string.Empty;

    public string? Brand { get; init; }

    public string? Model { get; init; }

    public string? Notes { get; init; }
}

public sealed record CreatePlanRequestDto
{
    public string Name { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;
}

public sealed record TrainingPlanDto : CloudEntityDto
{
    public string Name { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public bool IsBuiltIn { get; init; }
}

public sealed record CreatePlanVersionRequestDto
{
    public Guid? BasePlanVersionId { get; init; }

    public int IntroWeeks { get; init; } = 2;

    public int IntroMaxSets { get; init; } = 2;

    public IReadOnlyList<PlanDayDto> Days { get; init; } = Array.Empty<PlanDayDto>();

    public string? SnapshotJson { get; init; }
}

public sealed record UpdatePlanVersionRequestDto
{
    public int? IntroWeeks { get; init; }

    public int? IntroMaxSets { get; init; }

    public IReadOnlyList<PlanDayDto>? Days { get; init; }

    public string? SnapshotJson { get; init; }
}

public sealed record PublishPlanVersionRequestDto
{
    /// <summary>Optional optimistic concurrency token.</summary>
    public long? ExpectedVersion { get; init; }
}

public sealed record PlanAssignmentUpsertDto
{
    public Guid UserId { get; init; }

    public Guid PlanVersionId { get; init; }

    public DateOnly StartLocalDate { get; init; }

    public DateOnly? EndLocalDate { get; init; }

    public bool IsActive { get; init; } = true;
}

public sealed record AuditLogDto : CloudEntityDto
{
    public Guid? ActorUserId { get; init; }

    public string Action { get; init; } = string.Empty;

    public string EntityType { get; init; } = string.Empty;

    public Guid? EntityId { get; init; }

    public JsonElement? Before { get; init; }

    public JsonElement? After { get; init; }

    public string? IpAddress { get; init; }
}

public sealed record AuditLogPageDto : CursorPageDto
{
    public IReadOnlyList<AuditLogDto> Items { get; init; } = Array.Empty<AuditLogDto>();
}

public sealed record AdminSyncStatusDto
{
    public DateTimeOffset? ServerTime { get; init; }

    public long PendingOperations { get; init; }

    public long FailedOperations { get; init; }

    public string Status { get; init; } = "healthy";

    public string? Message { get; init; }
}
